using DisputePortal.Api.Data;
using DisputePortal.Api.DTOs;
using DisputePortal.Api.Events;
using DisputePortal.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace DisputePortal.Api.Services;

/// <summary>
/// Sends an agent's decision to Kafka through the outbox. It never touches the dispute: decisions
/// reach a dispute only through <see cref="DecisionConsumer"/> and <see cref="DecisionHandler"/>.
/// It deliberately does not check the state machine either, so a move that cannot apply is judged
/// by the consumer and ends up stale or on the dead-letter topic, where a demo can show it.
/// </summary>
public sealed class AgentDecisionService
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;
    private readonly ILogger<AgentDecisionService> _logger;

    public AgentDecisionService(AppDbContext db, TimeProvider clock, ILogger<AgentDecisionService> logger)
    {
        _db = db;
        _clock = clock;
        _logger = logger;
    }

    public async Task<ServiceResult<AgentDecisionResponse>> SendAsync(
        Guid agentId, Guid disputeId, DisputeDecision decision, string? note, CancellationToken ct)
    {
        var dispute = await _db.Disputes.AsNoTracking().FirstOrDefaultAsync(d => d.Id == disputeId, ct);
        if (dispute is null)
        {
            _logger.LogInformation("Agent decision: dispute {DisputeId} not found", disputeId);
            return ServiceResult<AgentDecisionResponse>.Fail(ServiceErrorKind.NotFound, "dispute_not_found", "Dispute not found.");
        }

        var (topic, eventType) = DisputeTopics.ForDecision(decision);
        var trimmed = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        var @event = DisputeEvent.Decision(dispute, eventType, trimmed, _clock.GetUtcNow().UtcDateTime);

        _db.OutboxMessages.Add(OutboxMessage.For(topic, @event));
        await _db.SaveChangesAsync(ct);

        // Never the note: it is free text.
        _logger.LogInformation("Agent {AgentId} queued {EventType} {EventId} for dispute {DisputeId} on {Topic}; dispute is {Status}",
            agentId, eventType, @event.EventId, disputeId, topic, dispute.Status);
        return ServiceResult<AgentDecisionResponse>.Ok(new AgentDecisionResponse(@event.EventId, eventType, topic));
    }
}
