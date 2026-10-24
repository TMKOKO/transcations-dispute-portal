using System.ComponentModel.DataAnnotations;
using DisputePortal.Api.DTOs;
using DisputePortal.Api.Models;
using DisputePortal.Api.Services;

namespace DisputePortal.UnitTests;

public class RequestValidationTests
{
    private static List<ValidationResult> Validate(object model)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(model, new ValidationContext(model), results, validateAllProperties: true);
        return results;
    }

    private static RaiseDisputeRequest Valid() => new()
    {
        TransactionId  = Guid.NewGuid(),
        Reason         = DisputeReason.DuplicateCharge,
        Description    = "I was charged twice for one purchase.",
        DisputedAmount = 100.50m,
    };

    [Fact]
    public void Valid_request_passes()
    {
        Assert.Empty(Validate(Valid()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Amount_must_be_positive(decimal amount)
    {
        var r = Valid();
        r.DisputedAmount = amount;
        Assert.Contains(Validate(r), v => v.MemberNames.Contains(nameof(RaiseDisputeRequest.DisputedAmount)));
    }

    [Fact]
    public void Amount_rejects_more_than_two_decimal_places()
    {
        var r = Valid();
        r.DisputedAmount = 10.005m;
        Assert.Contains(Validate(r), v => v.ErrorMessage!.Contains("two decimal places"));
    }

    [Fact]
    public void Description_shorter_than_minimum_is_rejected()
    {
        var r = Valid();
        r.Description = new string('x', RaiseDisputeRequest.DescriptionMinLength - 1);
        Assert.NotEmpty(Validate(r));
    }

    [Fact]
    public void Description_padded_with_whitespace_to_the_minimum_is_rejected()
    {
        var r = Valid();
        r.Description = "too short" + new string(' ', 20);
        var errors = Validate(r);
        Assert.Single(errors);
    }

    [Fact]
    public void Missing_fields_are_required_not_defaulted()
    {
        var errors = Validate(new RaiseDisputeRequest());
        var members = errors.SelectMany(e => e.MemberNames).ToHashSet();

        Assert.Contains(nameof(RaiseDisputeRequest.TransactionId), members);
        Assert.Contains(nameof(RaiseDisputeRequest.Reason), members);
        Assert.Contains(nameof(RaiseDisputeRequest.DisputedAmount), members);
    }

    [Fact]
    public void Transaction_query_rejects_from_after_to()
    {
        var q = new TransactionQuery { From = new DateOnly(2026, 10, 2), To = new DateOnly(2026, 10, 1) };
        Assert.NotEmpty(Validate(q));
    }

    [Theory]
    [InlineData(2026, 1042, "DSP-2026-001042")]
    [InlineData(2027, 1, "DSP-2027-000001")]
    [InlineData(2026, 1234567, "DSP-2026-1234567")]
    public void Dispute_number_format(int year, long sequence, string expected)
    {
        Assert.Equal(expected, DisputeNumber.Format(year, sequence));
    }
}
