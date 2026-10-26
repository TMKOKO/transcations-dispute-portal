using DisputePortal.Api.Models;
using DisputePortal.Api.Services;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace DisputePortal.UnitTests;

public class DisputeEligibilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private readonly DisputeEligibility _sut = new(
        Options.Create(new DisputeOptions { WindowDays = 120 }),
        new FakeTimeProvider(Now));

    private static Transaction Tx(
        TransactionType type = TransactionType.Debit,
        TransactionStatus status = TransactionStatus.Settled,
        TimeSpan? age = null) => new()
    {
        Amount          = 500m,
        Type            = type,
        Status          = status,
        TransactionDate = Now.UtcDateTime - (age ?? TimeSpan.FromDays(5)),
    };

    [Fact]
    public void Settled_recent_debit_with_no_dispute_is_eligible()
    {
        var result = _sut.Evaluate(Tx(), latestDisputeStatus: null);

        Assert.True(result.CanDispute);
        Assert.Null(result.Reason);
        Assert.Null(result.Message);
    }

    [Theory]
    [InlineData(DisputeStatus.Submitted)]
    [InlineData(DisputeStatus.UnderReview)]
    public void Open_dispute_blocks_a_new_one(DisputeStatus open)
    {
        var result = _sut.Evaluate(Tx(), open);
        Assert.Equal(IneligibleReason.ActiveDispute, result.Reason);
    }

    [Fact]
    public void Resolved_dispute_blocks_a_new_one()
    {
        var result = _sut.Evaluate(Tx(), DisputeStatus.Resolved);
        Assert.Equal(IneligibleReason.AlreadyResolved, result.Reason);
    }

    [Theory]
    [InlineData(DisputeStatus.Rejected)]
    [InlineData(DisputeStatus.Withdrawn)]
    public void Rejected_or_withdrawn_dispute_can_be_raised_again(DisputeStatus closed)
    {
        Assert.True(_sut.Evaluate(Tx(), closed).CanDispute);
    }

    [Fact]
    public void Credits_are_not_eligible()
    {
        Assert.Equal(IneligibleReason.NotADebit, _sut.Evaluate(Tx(type: TransactionType.Credit), null).Reason);
    }

    [Fact]
    public void Reversed_transactions_are_not_eligible()
    {
        Assert.Equal(IneligibleReason.Reversed, _sut.Evaluate(Tx(status: TransactionStatus.Reversed), null).Reason);
    }

    [Fact]
    public void Pending_transactions_are_not_eligible()
    {
        Assert.Equal(IneligibleReason.Pending, _sut.Evaluate(Tx(status: TransactionStatus.Pending), null).Reason);
    }

    [Fact]
    public void Exactly_120_days_old_is_still_eligible()
    {
        Assert.True(_sut.Evaluate(Tx(age: TimeSpan.FromDays(120)), null).CanDispute);
    }

    [Fact]
    public void One_second_past_120_days_is_outside_the_window()
    {
        var result = _sut.Evaluate(Tx(age: TimeSpan.FromDays(120) + TimeSpan.FromSeconds(1)), null);

        Assert.Equal(IneligibleReason.OutsideWindow, result.Reason);
        Assert.Contains("120 days", result.Message);
    }

    [Fact]
    public void Day_121_is_outside_the_window()
    {
        Assert.Equal(IneligibleReason.OutsideWindow, _sut.Evaluate(Tx(age: TimeSpan.FromDays(121)), null).Reason);
    }

    [Fact]
    public void Window_is_configurable()
    {
        var sut = new DisputeEligibility(Options.Create(new DisputeOptions { WindowDays = 30 }), new FakeTimeProvider(Now));
        Assert.Equal(IneligibleReason.OutsideWindow, sut.Evaluate(Tx(age: TimeSpan.FromDays(31)), null).Reason);
    }

    [Fact]
    public void Open_dispute_is_reported_ahead_of_other_reasons()
    {
        // Old and open: the customer most needs to know a case already exists.
        var result = _sut.Evaluate(Tx(age: TimeSpan.FromDays(200)), DisputeStatus.Submitted);
        Assert.Equal(IneligibleReason.ActiveDispute, result.Reason);
    }
}
