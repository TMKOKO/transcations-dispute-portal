using System.ComponentModel.DataAnnotations;
using DisputePortal.Api.Models;

namespace DisputePortal.Api.DTOs;

public class RaiseDisputeRequest : IValidatableObject
{
    // Mirrored in web/src/lib/disputeRules.ts — change both together, or the form will let
    // through descriptions the API then rejects.
    public const int DescriptionMinLength = 20;
    public const int DescriptionMaxLength = 1000;

    // Nullable + [Required] so a missing field is a 400, rather than silently binding to
    // Guid.Empty / the first enum member / 0.
    [Required]
    public Guid? TransactionId { get; set; }

    [Required]
    [EnumDataType(typeof(DisputeReason))]
    public DisputeReason? Reason { get; set; }

    [Required]
    [StringLength(DescriptionMaxLength, MinimumLength = DescriptionMinLength,
        ErrorMessage = "Description must be between 20 and 1000 characters.")]
    public string Description { get; set; } = "";

    // The upper bound against the transaction amount is a business rule, checked in DisputeService.
    [Required]
    [Range(typeof(decimal), "0.01", "9999999999.99", ErrorMessage = "Disputed amount must be greater than zero.")]
    public decimal? DisputedAmount { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (DisputedAmount is { } amount && decimal.Round(amount, 2) != amount)
            yield return new ValidationResult("Disputed amount cannot have more than two decimal places.",
                [nameof(DisputedAmount)]);

        // [StringLength] counts raw characters, so twenty spaces would pass it. The service stores the
        // trimmed text, so measure that. (Raw-length failures are already reported above; don't repeat them.)
        if (Description.Length >= DescriptionMinLength && Description.Trim().Length < DescriptionMinLength)
            yield return new ValidationResult("Description must be between 20 and 1000 characters.", [nameof(Description)]);
    }
}

public class DisputeListQuery
{
    public DisputeStatus? Status { get; set; }

    [Range(1, 10_000)]
    public int Page { get; set; } = 1;

    [Range(1, 100)]
    public int PageSize { get; set; } = 20;
}

public class WithdrawDisputeRequest
{
    [StringLength(500)]
    public string? Note { get; set; }
}

public class AgentStatusUpdateRequest
{
    [Required]
    [EnumDataType(typeof(DisputeStatus))]
    public DisputeStatus? Status { get; set; }

    // Required by DisputeService for Resolved/Rejected: the customer is owed a reason.
    [StringLength(500)]
    public string? Note { get; set; }
}

public record TransactionBrief(Guid Id, string Reference, string MerchantName, decimal Amount, string Currency, DateTime TransactionDate)
{
    public static TransactionBrief From(Transaction t) =>
        new(t.Id, t.Reference, t.MerchantName, t.Amount, t.Currency, t.TransactionDate);
}

public record DisputeSummaryResponse(
    Guid Id,
    string DisputeNumber,
    DisputeStatus Status,
    DisputeReason Reason,
    decimal DisputedAmount,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    TransactionBrief Transaction)
{
    public static DisputeSummaryResponse From(Dispute d) => new(
        d.Id, d.DisputeNumber, d.Status, d.Reason, d.DisputedAmount, d.CreatedAt, d.UpdatedAt,
        TransactionBrief.From(d.Transaction!));
}

public record TimelineEntry(DisputeStatus? FromStatus, DisputeStatus ToStatus, string? Note, string ChangedBy, DateTime ChangedAt);

public record DisputeDetailResponse(
    Guid Id,
    string DisputeNumber,
    DisputeStatus Status,
    DisputeReason Reason,
    string Description,
    decimal DisputedAmount,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? ResolvedAt,
    bool CanWithdraw,
    TransactionBrief Transaction,
    IReadOnlyList<TimelineEntry> Timeline)
{
    public static DisputeDetailResponse From(Dispute d) => new(
        d.Id,
        d.DisputeNumber,
        d.Status,
        d.Reason,
        d.Description,
        d.DisputedAmount,
        d.CreatedAt,
        d.UpdatedAt,
        d.ResolvedAt,
        d.CanTransitionTo(DisputeStatus.Withdrawn),
        TransactionBrief.From(d.Transaction!),
        d.History
            .OrderBy(h => h.ChangedAt)
            .ThenBy(h => h.Id)
            .Select(h => new TimelineEntry(h.FromStatus, h.ToStatus, h.Note, h.ChangedBy, h.ChangedAt))
            .ToList());
}
