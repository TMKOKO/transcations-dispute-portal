using DisputePortal.Api.Models;

namespace DisputePortal.Api.Events;

/// <summary>
/// Kafka topics for dispute events, one per event type. Every message is keyed by dispute id, so
/// the events for one dispute land on one partition of each topic, in order.
/// Created by docker/kafka/create-topics.sh.
/// </summary>
public static class DisputeTopics
{
    // API -> back office.
    public const string Submitted = "dispute-submitted";
    public const string Withdrawn = "dispute-withdrawn";

    // Back office -> API: the bank's decisions.
    public const string UnderReview  = "dispute-under-review";
    public const string RefundIssued = "dispute-refund-issued";
    public const string Rejected     = "dispute-rejected";

    /// <summary>Decisions the API could not apply, with the reason in a header, for someone to look at.</summary>
    public const string DecisionsDeadLetter = "dispute-decisions-dlq";

    public static readonly string[] Decisions = [UnderReview, RefundIssued, Rejected];

    /// <summary>The event type a decision topic carries and the status it moves a dispute to; null for any other topic.</summary>
    public static (string EventType, DisputeStatus Status)? DecisionFor(string topic) => topic switch
    {
        UnderReview  => (DisputeEventTypes.UnderReview, DisputeStatus.UnderReview),
        // The customer-facing status for a refund is Resolved; the event says what actually happened.
        RefundIssued => (DisputeEventTypes.RefundIssued, DisputeStatus.Resolved),
        Rejected     => (DisputeEventTypes.Rejected, DisputeStatus.Rejected),
        _ => null
    };
}

public static class DisputeEventTypes
{
    public const string DisputeSubmitted = "DisputeSubmitted";
    public const string DisputeWithdrawn = "DisputeWithdrawn";
    public const string UnderReview      = "UnderReview";
    public const string RefundIssued     = "RefundIssued";
    public const string Rejected         = "Rejected";
}
