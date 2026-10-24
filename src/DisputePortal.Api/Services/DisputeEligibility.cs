using DisputePortal.Api.Models;
using Microsoft.Extensions.Options;

namespace DisputePortal.Api.Services;

public enum IneligibleReason
{
    ActiveDispute,
    AlreadyResolved,
    NotADebit,
    Reversed,
    Pending,
    OutsideWindow
}

public sealed record EligibilityResult(bool CanDispute, IneligibleReason? Reason, string? Message)
{
    public static readonly EligibilityResult Eligible = new(true, null, null);

    public static EligibilityResult No(IneligibleReason reason, string message) => new(false, reason, message);
}

/// <summary>
/// The single definition of "can this transaction be disputed?". The transactions list uses it to
/// decide whether to offer the Dispute button, and <see cref="DisputeService"/> uses it again on
/// submit, so the UI can never offer something the API will then refuse (or the reverse).
/// </summary>
public sealed class DisputeEligibility
{
    private readonly DisputeOptions _options;
    private readonly TimeProvider _clock;

    public DisputeEligibility(IOptions<DisputeOptions> options, TimeProvider clock)
    {
        _options = options.Value;
        _clock   = clock;
    }

    public int WindowDays => _options.WindowDays;

    /// <param name="transaction">A transaction already scoped to the calling customer.</param>
    /// <param name="latestDisputeStatus">Status of the most recent dispute on it, or null if none.</param>
    public EligibilityResult Evaluate(Transaction transaction, DisputeStatus? latestDisputeStatus)
    {
        // Dispute history first: "you already have one open" is more useful than any other reason.
        if (latestDisputeStatus is { } status)
        {
            if (Dispute.IsActiveStatus(status))
                return EligibilityResult.No(IneligibleReason.ActiveDispute,
                    "This transaction already has an open dispute.");

            // Rejected or withdrawn disputes may be raised again (the customer may have new
            // information). A resolved one is closed for good. Resolved is terminal, so nothing
            // can follow it and the latest dispute is enough to detect it.
            if (status == DisputeStatus.Resolved)
                return EligibilityResult.No(IneligibleReason.AlreadyResolved,
                    "A dispute on this transaction has already been resolved.");
        }

        if (transaction.Type != TransactionType.Debit)
            return EligibilityResult.No(IneligibleReason.NotADebit,
                "Only payments out of your account can be disputed.");

        if (transaction.Status == TransactionStatus.Reversed)
            return EligibilityResult.No(IneligibleReason.Reversed,
                "This transaction was reversed, so there is nothing to dispute.");

        if (transaction.Status == TransactionStatus.Pending)
            return EligibilityResult.No(IneligibleReason.Pending,
                "Pending transactions can be disputed once they have settled.");

        var age = _clock.GetUtcNow().UtcDateTime - transaction.TransactionDate;
        if (age > TimeSpan.FromDays(_options.WindowDays))
            return EligibilityResult.No(IneligibleReason.OutsideWindow,
                $"Transactions can only be disputed within {_options.WindowDays} days.");

        return EligibilityResult.Eligible;
    }
}
