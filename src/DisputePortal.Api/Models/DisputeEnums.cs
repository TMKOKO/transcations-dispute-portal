namespace DisputePortal.Api.Models;

public enum DisputeStatus
{
    Submitted,
    UnderReview,
    Resolved,
    Rejected,
    Withdrawn
}

/// <summary>What an agent can decide. Each one goes out on its own Kafka topic (DisputeTopics).</summary>
public enum DisputeDecision
{
    UnderReview,
    RefundIssued,
    Rejected
}

public enum DisputeReason
{
    Unauthorised,
    DuplicateCharge,
    IncorrectAmount,
    GoodsNotReceived,
    CancelledSubscription,
    Other
}
