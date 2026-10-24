using DisputePortal.Api.Models;

namespace DisputePortal.UnitTests;

public class DisputeStateMachineTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);

    private static Dispute NewDispute() => Dispute.Raise(
        "DSP-2026-001000", Guid.NewGuid(), Guid.NewGuid(), DisputeReason.DuplicateCharge,
        "Charged twice for the same purchase.", 100m, "key-00000001", T0);

    private static Dispute InStatus(DisputeStatus status)
    {
        var d = NewDispute();
        switch (status)
        {
            case DisputeStatus.Submitted: break;
            case DisputeStatus.UnderReview:
                d.TransitionTo(DisputeStatus.UnderReview, DisputeActor.Agent, null, T0.AddHours(1));
                break;
            case DisputeStatus.Resolved:
                d.TransitionTo(DisputeStatus.UnderReview, DisputeActor.Agent, null, T0.AddHours(1));
                d.TransitionTo(DisputeStatus.Resolved, DisputeActor.Agent, "Refunded", T0.AddHours(2));
                break;
            default:
                d.TransitionTo(status, DisputeActor.Agent, "note", T0.AddHours(1));
                break;
        }
        return d;
    }

    [Fact]
    public void Raise_starts_submitted_with_one_timeline_row()
    {
        var d = NewDispute();

        Assert.Equal(DisputeStatus.Submitted, d.Status);
        Assert.True(d.IsActive);
        var row = Assert.Single(d.History);
        Assert.Null(row.FromStatus);
        Assert.Equal(DisputeStatus.Submitted, row.ToStatus);
        Assert.Equal(DisputeActor.Customer, row.ChangedBy);
        Assert.Equal(T0, row.ChangedAt);
        Assert.Null(d.ResolvedAt);
    }

    public static TheoryData<DisputeStatus, DisputeStatus> LegalMoves => new()
    {
        { DisputeStatus.Submitted,   DisputeStatus.UnderReview },
        { DisputeStatus.Submitted,   DisputeStatus.Rejected },
        { DisputeStatus.Submitted,   DisputeStatus.Withdrawn },
        { DisputeStatus.UnderReview, DisputeStatus.Resolved },
        { DisputeStatus.UnderReview, DisputeStatus.Rejected },
        { DisputeStatus.UnderReview, DisputeStatus.Withdrawn },
    };

    [Theory]
    [MemberData(nameof(LegalMoves))]
    public void Legal_moves_change_status_and_append_history(DisputeStatus from, DisputeStatus to)
    {
        var d = InStatus(from);
        var historyBefore = d.History.Count;
        var at = T0.AddDays(3);

        var row = d.TransitionTo(to, DisputeActor.Agent, "  reason  ", at);

        Assert.Equal(to, d.Status);
        Assert.Equal(historyBefore + 1, d.History.Count);
        Assert.Equal(from, row.FromStatus);
        Assert.Equal(to, row.ToStatus);
        Assert.Equal("reason", row.Note);   // trimmed
        Assert.Equal(at, d.UpdatedAt);
    }

    [Fact]
    public void Every_status_pair_not_listed_as_legal_is_rejected()
    {
        var legal = LegalMoves.Select(x => ((DisputeStatus)x[0], (DisputeStatus)x[1])).ToHashSet();
        var all = Enum.GetValues<DisputeStatus>();

        foreach (var from in all)
        foreach (var to in all)
        {
            if (legal.Contains((from, to))) continue;

            var d = InStatus(from);
            var historyBefore = d.History.Count;

            var ex = Assert.Throws<InvalidDisputeTransitionException>(
                () => d.TransitionTo(to, DisputeActor.Agent, null, T0.AddDays(5)));

            Assert.Equal(from, ex.From);
            Assert.Equal(to, ex.To);
            Assert.Equal(from, d.Status);                  // unchanged
            Assert.Equal(historyBefore, d.History.Count);  // nothing appended
        }
    }

    [Theory]
    [InlineData(DisputeStatus.Resolved)]
    [InlineData(DisputeStatus.Rejected)]
    [InlineData(DisputeStatus.Withdrawn)]
    public void Terminal_statuses_set_resolved_at_and_are_inactive(DisputeStatus terminal)
    {
        var d = InStatus(terminal);

        Assert.True(Dispute.IsTerminal(terminal));
        Assert.False(d.IsActive);
        Assert.NotNull(d.ResolvedAt);
    }

    [Fact]
    public void Blank_note_is_stored_as_null()
    {
        var d = NewDispute();
        var row = d.TransitionTo(DisputeStatus.Withdrawn, DisputeActor.Customer, "   ", T0.AddHours(1));
        Assert.Null(row.Note);
    }
}
