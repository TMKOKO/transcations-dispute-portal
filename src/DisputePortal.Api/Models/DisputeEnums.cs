namespace DisputePortal.Api.Models;

public enum DisputeStatus
{
    Submitted,
    UnderReview,
    Resolved,
    Rejected,
    Withdrawn
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
