using DisputePortal.Api.Data;
using DisputePortal.Api.DTOs;
using DisputePortal.Api.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DisputePortal.Api.Services;

/// <param name="Dispute">Loaded with its transaction and history.</param>
/// <param name="Replayed">True when the idempotency key matched an earlier submit and nothing new was created.</param>
public sealed record RaiseDisputeOutcome(Dispute Dispute, bool Replayed);

public interface IDisputeService
{
    Task<ServiceResult<RaiseDisputeOutcome>> RaiseAsync(
        Guid customerId, string idempotencyKey, RaiseDisputeRequest request, CancellationToken ct);

    Task<ServiceResult<Dispute>> WithdrawAsync(Guid customerId, Guid disputeId, string? note, CancellationToken ct);
}

/// <summary>
/// Every write to a dispute goes through here. Reads stay in the controllers, where they are
/// plain scoped queries; this class owns the rules that can actually be broken.
/// </summary>
public sealed class DisputeService : IDisputeService
{
    private const string UniqueViolation = "23505";

    private readonly AppDbContext _db;
    private readonly DisputeEligibility _eligibility;
    private readonly TimeProvider _clock;
    private readonly ILogger<DisputeService> _logger;

    public DisputeService(AppDbContext db, DisputeEligibility eligibility, TimeProvider clock, ILogger<DisputeService> logger)
    {
        _db          = db;
        _eligibility = eligibility;
        _clock       = clock;
        _logger      = logger;
    }

    public async Task<ServiceResult<RaiseDisputeOutcome>> RaiseAsync(
        Guid customerId, string idempotencyKey, RaiseDisputeRequest request, CancellationToken ct)
    {
        var transactionId = request.TransactionId!.Value;

        // A replay of a submit we already accepted: hand back the original, create nothing.
        var replay = await ReplayAsync(customerId, idempotencyKey, transactionId, ct);
        if (replay is not null) return replay;

        // Scoped to the caller, so another customer's transaction is indistinguishable from one
        // that does not exist. Returning 403 instead would confirm the id was real.
        var transaction = await _db.Transactions
            .FirstOrDefaultAsync(t => t.Id == transactionId && t.CustomerId == customerId, ct);
        if (transaction is null)
        {
            _logger.LogInformation("Raise: transaction {TransactionId} not found for customer {CustomerId}", transactionId, customerId);
            return ServiceResult<RaiseDisputeOutcome>.Fail(ServiceErrorKind.NotFound,
                "transaction_not_found", "Transaction not found.");
        }
        _logger.LogInformation("Raise: loaded transaction {TransactionId} ({Status} {Type}, {Amount} {Currency}) for customer {CustomerId}",
            transactionId, transaction.Status, transaction.Type, transaction.Amount, transaction.Currency, customerId);

        var latestStatus = await _db.Disputes
            .Where(d => d.TransactionId == transactionId)
            .OrderByDescending(d => d.CreatedAt)
            .Select(d => (DisputeStatus?)d.Status)
            .FirstOrDefaultAsync(ct);
        _logger.LogInformation("Raise: latest dispute on transaction {TransactionId} is {LatestStatus}",
            transactionId, latestStatus?.ToString() ?? "none");

        var eligibility = _eligibility.Evaluate(transaction, latestStatus);
        if (!eligibility.CanDispute)
        {
            var kind = eligibility.Reason == IneligibleReason.ActiveDispute
                ? ServiceErrorKind.Conflict
                : ServiceErrorKind.BusinessRule;
            return ServiceResult<RaiseDisputeOutcome>.Fail(kind, ToCode(eligibility.Reason!.Value), eligibility.Message!);
        }

        var amount = request.DisputedAmount!.Value;
        if (amount > transaction.Amount)
            return ServiceResult<RaiseDisputeOutcome>.Fail(ServiceErrorKind.BusinessRule,
                "amount_exceeds_transaction",
                $"You can dispute at most {transaction.Amount:0.00} {transaction.Currency}, the full transaction amount.");

        var now = _clock.GetUtcNow().UtcDateTime;
        var sequence = await _db.NextDisputeSequenceAsync(ct);
        _logger.LogInformation("Raise: allocated dispute sequence {Sequence}", sequence);

        var dispute = Dispute.Raise(
            DisputeNumber.Format(now.Year, sequence),
            customerId,
            transactionId,
            request.Reason!.Value,
            request.Description.Trim(),
            amount,
            idempotencyKey,
            now);

        _db.Disputes.Add(dispute);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation } pg)
        {
            // Lost a race the up-front checks could not see. Detach the failed insert so the
            // follow-up query reads the winner from the database rather than our tracked copy.
            _db.ChangeTracker.Clear();
            _logger.LogWarning("Raise: insert for transaction {TransactionId} lost a race on {Constraint}",
                transactionId, pg.ConstraintName);

            if (pg.ConstraintName == AppDbContext.IdempotencyIndex)
            {
                var winner = await ReplayAsync(customerId, idempotencyKey, transactionId, ct);
                if (winner is not null) return winner;
            }

            if (pg.ConstraintName == AppDbContext.ActiveDisputeIndex)
                return ServiceResult<RaiseDisputeOutcome>.Fail(ServiceErrorKind.Conflict,
                    ToCode(IneligibleReason.ActiveDispute), "This transaction already has an open dispute.");

            _logger.LogError("Raise: unexpected unique violation on {Constraint}; rethrowing", pg.ConstraintName);
            throw;
        }

        dispute.Transaction = transaction;

        // Ids only. The description is free text and may well contain personal information.
        _logger.LogInformation("Dispute {DisputeNumber} ({DisputeId}) raised on transaction {TransactionId}",
            dispute.DisputeNumber, dispute.Id, transactionId);

        return ServiceResult<RaiseDisputeOutcome>.Ok(new RaiseDisputeOutcome(dispute, Replayed: false));
    }

    public async Task<ServiceResult<Dispute>> WithdrawAsync(Guid customerId, Guid disputeId, string? note, CancellationToken ct)
    {
        var dispute = await LoadAsync(d => d.Id == disputeId && d.CustomerId == customerId, ct);
        if (dispute is null)
        {
            _logger.LogInformation("Withdraw: dispute {DisputeId} not found for customer {CustomerId}", disputeId, customerId);
            return ServiceResult<Dispute>.Fail(ServiceErrorKind.NotFound, "dispute_not_found", "Dispute not found.");
        }
        _logger.LogInformation("Withdraw: loaded dispute {DisputeId}, status {Status}, for customer {CustomerId}",
            disputeId, dispute.Status, customerId);

        return await TransitionAsync(dispute, DisputeStatus.Withdrawn, DisputeActor.Customer, note, ct);
    }

    private async Task<ServiceResult<Dispute>> TransitionAsync(
        Dispute dispute, DisputeStatus to, string actor, string? note, CancellationToken ct)
    {
        if (!dispute.CanTransitionTo(to))
            return ServiceResult<Dispute>.Fail(ServiceErrorKind.Conflict, "invalid_transition",
                Dispute.IsTerminal(dispute.Status)
                    ? $"This dispute is already {Describe(dispute.Status)} and can no longer be changed."
                    : $"This dispute is {Describe(dispute.Status)} and cannot be moved to {Describe(to)}.");

        var from = dispute.Status;
        dispute.TransitionTo(to, actor, note, _clock.GetUtcNow().UtcDateTime);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            _logger.LogWarning("Dispute {DisputeId}: move {From} -> {To} lost to a concurrent update", dispute.Id, from, to);
            return ServiceResult<Dispute>.Fail(ServiceErrorKind.Conflict, "concurrent_update",
                "This dispute was updated by someone else. Refresh to see its latest status.");
        }

        _logger.LogInformation("Dispute {DisputeId} moved {From} -> {To} by {Actor}", dispute.Id, from, to, actor);
        return ServiceResult<Dispute>.Ok(dispute);
    }

    private async Task<ServiceResult<RaiseDisputeOutcome>?> ReplayAsync(
        Guid customerId, string idempotencyKey, Guid transactionId, CancellationToken ct)
    {
        // The key itself is never logged: it is a client secret of sorts, and the dispute id is enough.
        var existing = await LoadAsync(d => d.CustomerId == customerId && d.IdempotencyKey == idempotencyKey, ct);
        if (existing is null)
        {
            _logger.LogInformation("Raise: idempotency key is new for customer {CustomerId}", customerId);
            return null;
        }

        // Same key, different request: almost certainly a client bug. Refuse rather than return a
        // dispute for a transaction the caller did not ask about.
        if (existing.TransactionId != transactionId)
        {
            _logger.LogWarning("Raise: idempotency key already used by dispute {DisputeId} on transaction {OriginalTransactionId}, now sent for {TransactionId}",
                existing.Id, existing.TransactionId, transactionId);
            return ServiceResult<RaiseDisputeOutcome>.Fail(ServiceErrorKind.Conflict, "idempotency_key_reused",
                "This Idempotency-Key was already used for a different transaction.");
        }

        _logger.LogInformation("Raise: replayed dispute {DisputeNumber} ({DisputeId}) for a repeated idempotency key",
            existing.DisputeNumber, existing.Id);
        return ServiceResult<RaiseDisputeOutcome>.Ok(new RaiseDisputeOutcome(existing, Replayed: true));
    }

    private Task<Dispute?> LoadAsync(System.Linq.Expressions.Expression<Func<Dispute, bool>> predicate, CancellationToken ct) =>
        _db.Disputes
            .Include(d => d.Transaction)
            .Include(d => d.History)
            .FirstOrDefaultAsync(predicate, ct);

    internal static string ToCode(IneligibleReason reason) => reason switch
    {
        IneligibleReason.ActiveDispute   => "dispute_already_open",
        IneligibleReason.AlreadyResolved => "dispute_already_resolved",
        IneligibleReason.NotADebit       => "not_a_debit",
        IneligibleReason.Reversed        => "transaction_reversed",
        IneligibleReason.Pending         => "transaction_pending",
        IneligibleReason.OutsideWindow   => "outside_dispute_window",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, null)
    };

    private static string Describe(DisputeStatus status) => status switch
    {
        DisputeStatus.UnderReview => "under review",
        _ => status.ToString().ToLowerInvariant()
    };
}
