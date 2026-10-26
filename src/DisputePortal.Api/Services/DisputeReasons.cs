using DisputePortal.Api.Models;

namespace DisputePortal.Api.Services;

public sealed record DisputeReasonInfo(DisputeReason Code, string Label, string Description);

/// <summary>Customer-facing wording for each <see cref="DisputeReason"/>, served by <c>GET /api/dispute-reasons</c>.</summary>
public static class DisputeReasons
{
    public static readonly IReadOnlyList<DisputeReasonInfo> All =
    [
        new(DisputeReason.Unauthorised,          "I didn't make this transaction",  "You don't recognise it, or your card or details were used without permission."),
        new(DisputeReason.DuplicateCharge,       "I was charged more than once",    "The same purchase appears more than once."),
        new(DisputeReason.IncorrectAmount,       "The amount is wrong",             "You were charged a different amount from the one you agreed to."),
        new(DisputeReason.GoodsNotReceived,      "I didn't receive what I paid for", "The goods or service never arrived, or were not as described."),
        new(DisputeReason.CancelledSubscription, "I cancelled this subscription",   "You were billed after cancelling a subscription or debit order."),
        new(DisputeReason.Other,                 "Something else",                  "Tell us what happened in the description."),
    ];
}
