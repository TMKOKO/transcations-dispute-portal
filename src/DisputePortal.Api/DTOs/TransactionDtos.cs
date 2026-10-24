using System.ComponentModel.DataAnnotations;
using DisputePortal.Api.Models;
using DisputePortal.Api.Services;

namespace DisputePortal.Api.DTOs;

public class TransactionQuery : IValidatableObject
{
    /// <summary>Inclusive, as a SAST calendar date.</summary>
    public DateOnly? From { get; set; }

    /// <summary>Inclusive, as a SAST calendar date.</summary>
    public DateOnly? To { get; set; }

    /// <summary>Matches merchant name or reference, case-insensitive.</summary>
    [StringLength(100)]
    public string? Search { get; set; }

    [Range(1, 10_000)]
    public int Page { get; set; } = 1;

    // Capped so one request cannot pull a customer's entire history.
    [Range(1, 100)]
    public int PageSize { get; set; } = 20;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (From is { } from && To is { } to && from > to)
            yield return new ValidationResult("'from' must be on or before 'to'.", [nameof(From), nameof(To)]);
    }
}

public record DisputeRef(Guid Id, string DisputeNumber, DisputeStatus Status);

public record TransactionResponse(
    Guid Id,
    string Reference,
    string MerchantName,
    string MerchantCategory,
    decimal Amount,
    string Currency,
    TransactionType Type,
    TransactionChannel Channel,
    TransactionStatus Status,
    string? CardLast4,
    DateTime TransactionDate,
    bool CanDispute,
    IneligibleReason? IneligibleCode,
    string? IneligibleMessage,
    DisputeRef? LatestDispute)
{
    public static TransactionResponse From(Transaction t, EligibilityResult eligibility, DisputeRef? latestDispute) => new(
        t.Id,
        t.Reference,
        t.MerchantName,
        t.MerchantCategory,
        t.Amount,
        t.Currency,
        t.Type,
        t.Channel,
        t.Status,
        t.CardLast4,
        t.TransactionDate,
        eligibility.CanDispute,
        eligibility.Reason,
        eligibility.Message,
        latestDispute);
}
