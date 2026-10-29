using System.Text.Json;
using DisputePortal.Api.Data;
using DisputePortal.Api.Helpers;
using DisputePortal.Api.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DisputePortal.Api.Events;

public enum DecisionOutcome
{
    /// <summary>The dispute moved and its timeline has the new row.</summary>
    Applied,
    /// <summary>This event id was handled before (at-least-once redelivery). Nothing changed.</summary>
    Duplicate,
    /// <summary>The dispute was already past this step (a late or repeated decision, or the customer withdrew). Nothing changed.</summary>
    Stale,
    /// <summary>Cannot be applied as sent. The consumer parks it on the dead-letter topic.</summary>
    DeadLetter,
}

/// <param name="Outcome">What happened to the event.</param>
/// <param name="Reason">For <see cref="DecisionOutcome.DeadLetter"/>: a stable code, sent as a header on the DLQ message.</param>
public sealed record DecisionResult(DecisionOutcome Outcome, string? Reason = null)
{
    public static DecisionResult DeadLetter(string reason) => new(DecisionOutcome.DeadLetter, reason);
}

/// <summary>
/// Applies one back-office decision (UnderReview, RefundIssued, Rejected) to its dispute. Has no
/// Kafka types, so tests call it directly; <see cref="DecisionConsumer"/> feeds it.
/// Idempotent: the status change and the inbox row recording the event id commit in one SaveChanges.
/// Exceptions mean "try again later" (database down, lost every concurrency retry); everything that
/// would fail the same way every time is a result instead, so a bad message never blocks its partition.
/// </summary>
public sealed class DecisionHandler
{
    public const int NoteMaxLength = 500;   // DisputeStatusHistory.Note
    private const int ConcurrencyAttempts = 3;
    private const string UniqueViolation = "23505";

    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;
    private readonly ILogger<DecisionHandler> _logger;

    public DecisionHandler(AppDbContext db, TimeProvider clock, ILogger<DecisionHandler> logger)
    {
        _db = db;
        _clock = clock;
        _logger = logger;
    }

    public async Task<DecisionResult> HandleAsync(string topic, string payload, CancellationToken ct)
    {
        if (DisputeTopics.DecisionFor(topic) is not { } decision)
            return DeadLetter(topic, null, "unknown_topic");

        // Never log the payload: the note is free text written for the customer.
        DisputeEvent? e;
        try { e = DisputeEvent.FromJson(payload); }
        catch (JsonException) { return DeadLetter(topic, null, "malformed_event"); }

        if (e is null || e.EventId == Guid.Empty || e.DisputeId == Guid.Empty)
            return DeadLetter(topic, null, "malformed_event");
        if (e.SchemaVersion != DisputeEvent.CurrentSchemaVersion)
            return DeadLetter(topic, e, "unsupported_schema_version");
        if (e.EventType != decision.EventType)
            return DeadLetter(topic, e, "event_type_mismatch");
        if (e.Note is { } note && (note.Trim().Length > NoteMaxLength || note.Any(c => PlainTextAttribute.IsDisallowed(c, allowLineBreaks: true))))
            return DeadLetter(topic, e, "invalid_note");

        if (await _db.InboxMessages.AsNoTracking().AnyAsync(i => i.EventId == e.EventId, ct))
        {
            _logger.LogInformation("Decision {EventType} {EventId} for dispute {DisputeId} already handled; skipped",
                e.EventType, e.EventId, e.DisputeId);
            return new DecisionResult(DecisionOutcome.Duplicate);
        }

        for (var attempt = 1; ; attempt++)
        {
            _db.ChangeTracker.Clear();
            var dispute = await _db.Disputes.Include(d => d.History).FirstOrDefaultAsync(d => d.Id == e.DisputeId, ct);
            if (dispute is null)
                return DeadLetter(topic, e, "dispute_not_found");

            var from = dispute.Status;
            string outcome;
            if (dispute.CanTransitionTo(decision.Status))
            {
                dispute.TransitionTo(decision.Status, DisputeActor.Agent, e.Note, _clock.GetUtcNow().UtcDateTime);
                outcome = InboxOutcome.Applied;
            }
            else if (Dispute.IsTerminal(from) || from == decision.Status)
            {
                // Already decided, withdrawn, or this step already happened under another event id.
                outcome = InboxOutcome.Stale;
            }
            else
            {
                // A step the state machine does not allow yet, e.g. RefundIssued while still Submitted:
                // its UnderReview is missing or still on its way. A person has to look.
                return DeadLetter(topic, e, "transition_not_allowed");
            }

            _db.InboxMessages.Add(new InboxMessage
            {
                EventId     = e.EventId,
                EventType   = e.EventType,
                DisputeId   = e.DisputeId,
                Outcome     = outcome,
                ProcessedAt = _clock.GetUtcNow().UtcDateTime,
            });

            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException) when (attempt < ConcurrencyAttempts)
            {
                // The customer withdrew (or another instance applied something) between our read and
                // write. Reload and decide again against the new status.
                _logger.LogWarning("Decision {EventType} {EventId}: dispute {DisputeId} changed underneath it; retrying ({Attempt})",
                    e.EventType, e.EventId, e.DisputeId, attempt);
                continue;
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation, ConstraintName: AppDbContext.InboxPrimaryKey })
            {
                // Another consumer handled the same event id between our check and our write.
                _db.ChangeTracker.Clear();
                _logger.LogInformation("Decision {EventType} {EventId} for dispute {DisputeId} handled concurrently; skipped",
                    e.EventType, e.EventId, e.DisputeId);
                return new DecisionResult(DecisionOutcome.Duplicate);
            }

            if (outcome == InboxOutcome.Applied)
            {
                _logger.LogInformation("Applied {EventType} {EventId} to dispute {DisputeId}: {From} -> {To}",
                    e.EventType, e.EventId, e.DisputeId, from, decision.Status);
                return new DecisionResult(DecisionOutcome.Applied);
            }

            _logger.LogWarning("Decision {EventType} {EventId} is stale: dispute {DisputeId} is already {Status}; skipped",
                e.EventType, e.EventId, e.DisputeId, from);
            return new DecisionResult(DecisionOutcome.Stale);
        }
    }

    private DecisionResult DeadLetter(string topic, DisputeEvent? e, string reason)
    {
        _logger.LogWarning("Decision on {Topic} (event {EventId}, dispute {DisputeId}) cannot be applied: {Reason}",
            topic, e?.EventId, e?.DisputeId, reason);
        return DecisionResult.DeadLetter(reason);
    }
}
