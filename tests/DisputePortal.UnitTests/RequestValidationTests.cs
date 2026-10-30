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

    private static AgentDecisionRequest ValidDecision() => new()
    {
        DisputeId = Guid.NewGuid(),
        Decision  = DisputeDecision.UnderReview,
    };

    [Fact]
    public void Agent_decision_needs_only_a_dispute_and_a_decision()
    {
        Assert.Empty(Validate(ValidDecision()));
    }

    [Fact]
    public void Agent_decision_rejects_a_missing_dispute_or_decision()
    {
        var results = Validate(new AgentDecisionRequest());
        Assert.Contains(results, v => v.MemberNames.Contains(nameof(AgentDecisionRequest.DisputeId)));
        Assert.Contains(results, v => v.MemberNames.Contains(nameof(AgentDecisionRequest.Decision)));
    }

    [Theory]
    [InlineData("Paid back.\nThanks", true)]
    [InlineData("ok", true)]
    [InlineData("Bad\u0000note", false)]
    [InlineData("Bad‮note", false)]
    public void Agent_decision_note_must_be_plain_text(string note, bool valid)
    {
        var r = ValidDecision();
        r.Note = note;
        Assert.Equal(valid, Validate(r).Count == 0);
    }

    [Fact]
    public void Agent_decision_note_has_the_same_limit_as_the_consumer()
    {
        var r = ValidDecision();
        r.Note = new string('a', 500);
        Assert.Empty(Validate(r));
        r.Note = new string('a', 501);
        Assert.Contains(Validate(r), v => v.MemberNames.Contains(nameof(AgentDecisionRequest.Note)));
    }

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
    [InlineData("I was charged twice\u0000 for one purchase.")]
    [InlineData("I was charged twice ‮for one purchase.")]
    [InlineData("I was charged twice \u001b[31mfor one purchase.")]
    public void Description_rejects_control_and_bidi_characters(string description)
    {
        var r = Valid();
        r.Description = description;
        Assert.Contains(Validate(r), v => v.MemberNames.Contains(nameof(RaiseDisputeRequest.Description)));
    }

    [Fact]
    public void Description_allows_line_breaks_and_tabs()
    {
        var r = Valid();
        r.Description = "I was charged twice.\r\n\tOnce on the 3rd, again on the 4th.";
        Assert.Empty(Validate(r));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("ok", false)]
    [InlineData("  ok  ", false)]
    [InlineData("Refunded", true)]
    [InlineData("Paid back.\nThanks", true)]
    [InlineData("Refunded\u0000", false)]
    public void Withdraw_note_is_optional_but_must_say_something(string? note, bool valid)
    {
        var r = new WithdrawDisputeRequest { DisputeId = Guid.NewGuid(), Note = note };
        Assert.Equal(valid, Validate(r).Count == 0);
    }

    [Fact]
    public void Withdraw_note_has_a_maximum()
    {
        var r = new WithdrawDisputeRequest { DisputeId = Guid.NewGuid(), Note = new string('a', WithdrawDisputeRequest.NoteMaxLength + 1) };
        Assert.NotEmpty(Validate(r));
    }

    [Fact]
    public void Login_email_is_at_most_254_characters()
    {
        var ok = new LoginRequest { Email = new string('a', 242) + "@example.com", Password = "x" };
        var tooLong = new LoginRequest { Email = new string('a', 243) + "@example.com", Password = "x" };
        Assert.Empty(Validate(ok));
        Assert.Contains(Validate(tooLong), v => v.MemberNames.Contains(nameof(LoginRequest.Email)));
    }

    [Fact]
    public void Login_rejects_control_characters_in_the_email_and_a_blank_password()
    {
        Assert.NotEmpty(Validate(new LoginRequest { Email = "thandi@example.com\u0000", Password = "x" }));
        Assert.NotEmpty(Validate(new LoginRequest { Email = "thandi@example.com", Password = "   " }));
    }

    [Fact]
    public void Transaction_search_rejects_control_characters()
    {
        Assert.NotEmpty(Validate(new TransactionQuery { Search = "comp\tuticket" }));
        Assert.Empty(Validate(new TransactionQuery { Search = "computicket" }));
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
