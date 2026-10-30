using System.Text.Json;
using System.Text.Json.Serialization;
using DisputePortal.Api.Models;

namespace DisputePortal.Api.Events;

/// <summary>
/// The JSON envelope on every dispute topic, in both directions. Ids, codes and amounts only: the
/// dispute description never goes on a topic, where it would sit in a log nobody can redact. A
/// consumer that needs it reads the dispute by id (claim check).
/// </summary>
public sealed record DisputeEvent
{
    public const int CurrentSchemaVersion = 1;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Unique per event. Delivery is at least once, so consumers dedupe on this.</summary>
    public Guid EventId { get; init; }
    public string EventType { get; init; } = string.Empty;
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public DateTimeOffset OccurredAt { get; init; }

    public Guid DisputeId { get; init; }
    public string? DisputeNumber { get; init; }
    public Guid? CustomerId { get; init; }
    public Guid? TransactionId { get; init; }

    // DisputeSubmitted only.
    public string? Reason { get; init; }
    public decimal? Amount { get; init; }
    public string? Currency { get; init; }

    /// <summary>Back-office decisions only: shown to the customer on the dispute's timeline.</summary>
    public string? Note { get; init; }

    public static DisputeEvent Submitted(Dispute dispute, string currency, DateTime nowUtc) => new()
    {
        EventId       = Guid.NewGuid(),
        EventType     = DisputeEventTypes.DisputeSubmitted,
        OccurredAt    = new DateTimeOffset(nowUtc, TimeSpan.Zero),
        DisputeId     = dispute.Id,
        DisputeNumber = dispute.DisputeNumber,
        CustomerId    = dispute.CustomerId,
        TransactionId = dispute.TransactionId,
        Reason        = dispute.Reason.ToString(),
        Amount        = dispute.DisputedAmount,
        Currency      = currency,
    };

    // No note: the customer's withdraw note is free text, so it stays in the database like the description.
    public static DisputeEvent Withdrawn(Dispute dispute, DateTime nowUtc) => new()
    {
        EventId       = Guid.NewGuid(),
        EventType     = DisputeEventTypes.DisputeWithdrawn,
        OccurredAt    = new DateTimeOffset(nowUtc, TimeSpan.Zero),
        DisputeId     = dispute.Id,
        DisputeNumber = dispute.DisputeNumber,
        CustomerId    = dispute.CustomerId,
        TransactionId = dispute.TransactionId,
    };

    // The note is written by the agent for the customer, so unlike the customer's own text it may travel.
    public static DisputeEvent Decision(Dispute dispute, string eventType, string? note, DateTime nowUtc) => new()
    {
        EventId       = Guid.NewGuid(),
        EventType     = eventType,
        OccurredAt    = new DateTimeOffset(nowUtc, TimeSpan.Zero),
        DisputeId     = dispute.Id,
        DisputeNumber = dispute.DisputeNumber,
        CustomerId    = dispute.CustomerId,
        TransactionId = dispute.TransactionId,
        Note          = note,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <exception cref="JsonException">Not a JSON object of this shape.</exception>
    public static DisputeEvent? FromJson(string json) => JsonSerializer.Deserialize<DisputeEvent>(json, Json);
}
