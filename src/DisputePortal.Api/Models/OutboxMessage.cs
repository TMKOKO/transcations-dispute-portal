using DisputePortal.Api.Events;

namespace DisputePortal.Api.Models;

/// <summary>
/// An event waiting to go to Kafka (transactional outbox). Written in the same SaveChanges as the
/// dispute change it describes, so the two commit or roll back together: no event for a change that
/// never happened, and no change whose event is lost because Kafka was down. OutboxRelay publishes it.
/// </summary>
public class OutboxMessage
{
    public long Id { get; set; }

    public Guid EventId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string Topic { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;   // jsonb

    public DateTime OccurredAt { get; set; }
    public DateTime? PublishedAt { get; set; }

    public int Attempts { get; set; }
    public string? LastError { get; set; }

    public static OutboxMessage For(string topic, DisputeEvent @event) => new()
    {
        EventId    = @event.EventId,
        EventType  = @event.EventType,
        Topic      = topic,
        Key        = @event.DisputeId.ToString(),
        Payload    = @event.ToJson(),
        OccurredAt = @event.OccurredAt.UtcDateTime,
    };
}

/// <summary>
/// An event from Kafka that has been handled (idempotent consumer). Its primary key is the event id,
/// so a redelivered event is recognised and skipped; it is written in the same SaveChanges as the
/// status change the event caused.
/// </summary>
public class InboxMessage
{
    public Guid EventId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public Guid DisputeId { get; set; }

    /// <summary><see cref="InboxOutcome"/>: applied, or stale (the dispute had already moved past it).</summary>
    public string Outcome { get; set; } = string.Empty;
    public DateTime ProcessedAt { get; set; }
}

public static class InboxOutcome
{
    public const string Applied = "applied";
    public const string Stale   = "stale";
}
