using System.Text.Json;
using DisputePortal.Api.Data;
using DisputePortal.Api.Events;
using DisputePortal.Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace DisputePortal.UnitTests;

public class DisputeEventTests
{
    private static readonly DateTime Now = new(2026, 10, 29, 8, 0, 0, DateTimeKind.Utc);

    private static Dispute Raised() => Dispute.Raise("DSP-2026-001000", Guid.NewGuid(), Guid.NewGuid(),
        DisputeReason.Unauthorised, "Someone else used my card at this shop.", 250m, "key-1", Now);

    [Theory]
    [InlineData(DisputeDecision.UnderReview, DisputeStatus.UnderReview)]
    [InlineData(DisputeDecision.RefundIssued, DisputeStatus.Resolved)]
    [InlineData(DisputeDecision.Rejected, DisputeStatus.Rejected)]
    public void An_agents_decision_goes_to_the_topic_the_consumer_reads_it_from(DisputeDecision decision, DisputeStatus status)
    {
        var (topic, eventType) = DisputeTopics.ForDecision(decision);

        var read = DisputeTopics.DecisionFor(topic);

        Assert.NotNull(read);
        Assert.Equal(eventType, read.Value.EventType);
        Assert.Equal(status, read.Value.Status);
    }

    [Fact]
    public void An_agents_decision_carries_the_note_but_never_the_description()
    {
        var dispute = Raised();

        var json = DisputeEvent.Decision(dispute, DisputeEventTypes.UnderReview, "Looking into it.", Now).ToJson();
        var root = JsonDocument.Parse(json).RootElement;

        Assert.Equal("UnderReview", root.GetProperty("eventType").GetString());
        Assert.Equal(dispute.Id, root.GetProperty("disputeId").GetGuid());
        Assert.Equal("Looking into it.", root.GetProperty("note").GetString());
        Assert.False(root.TryGetProperty("description", out _));
        Assert.DoesNotContain(dispute.Description, json);
    }

    [Fact]
    public void DisputeSubmitted_carries_ids_reason_and_amount_but_never_the_description()
    {
        var dispute = Raised();

        var json = DisputeEvent.Submitted(dispute, "ZAR", Now).ToJson();
        var root = JsonDocument.Parse(json).RootElement;

        Assert.Equal("DisputeSubmitted", root.GetProperty("eventType").GetString());
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(dispute.Id, root.GetProperty("disputeId").GetGuid());
        Assert.Equal("Unauthorised", root.GetProperty("reason").GetString());
        Assert.Equal(250m, root.GetProperty("amount").GetDecimal());
        Assert.Equal("ZAR", root.GetProperty("currency").GetString());
        Assert.False(root.TryGetProperty("description", out _));
        Assert.DoesNotContain("Someone else used my card", json);
        // Unset fields are left out, not sent as null.
        Assert.False(root.TryGetProperty("note", out _));
    }

    [Fact]
    public void An_event_round_trips_through_json()
    {
        var original = DisputeEvent.Withdrawn(Raised(), Now);

        var copy = DisputeEvent.FromJson(original.ToJson());

        Assert.Equal(original, copy);
    }

    [Fact]
    public void Each_event_gets_its_own_id()
    {
        var dispute = Raised();

        Assert.NotEqual(DisputeEvent.Submitted(dispute, "ZAR", Now).EventId, DisputeEvent.Submitted(dispute, "ZAR", Now).EventId);
    }

    [Fact]
    public void Outbox_messages_are_keyed_by_dispute_so_its_events_stay_in_order()
    {
        var dispute = Raised();

        var message = OutboxMessage.For(DisputeTopics.Submitted, DisputeEvent.Submitted(dispute, "ZAR", Now));

        Assert.Equal(dispute.Id.ToString(), message.Key);
        Assert.Equal(DisputeTopics.Submitted, message.Topic);
        Assert.Equal(Now, message.OccurredAt);
    }

    [Theory]
    [InlineData(DisputeTopics.UnderReview, "UnderReview", DisputeStatus.UnderReview)]
    [InlineData(DisputeTopics.RefundIssued, "RefundIssued", DisputeStatus.Resolved)]
    [InlineData(DisputeTopics.Rejected, "Rejected", DisputeStatus.Rejected)]
    public void Each_decision_topic_maps_to_its_event_type_and_status(string topic, string eventType, DisputeStatus status)
    {
        Assert.Equal((eventType, status), DisputeTopics.DecisionFor(topic));
    }

    [Theory]
    [InlineData(DisputeTopics.Submitted)]
    [InlineData(DisputeTopics.Withdrawn)]
    [InlineData(DisputeTopics.DecisionsDeadLetter)]
    [InlineData("something-else")]
    public void Other_topics_are_not_decisions(string topic)
    {
        Assert.Null(DisputeTopics.DecisionFor(topic));
    }
}

public class ReadSideTests
{
    [Fact]
    public async Task The_read_context_refuses_to_save()
    {
        var options = new DbContextOptionsBuilder<ReadDbContext>().UseNpgsql("Host=localhost").Options;
        await using var db = new ReadDbContext(options);

        Assert.Throws<InvalidOperationException>(() => db.SaveChanges());
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public void Connection_failures_count_as_unavailable_but_query_errors_do_not()
    {
        Assert.True(ReadStore.IsUnavailable(new RetryLimitExceededException("gave up", new TimeoutException())));
        Assert.True(ReadStore.IsUnavailable(new InvalidOperationException("wrapped", new TimeoutException())));
        Assert.False(ReadStore.IsUnavailable(new InvalidOperationException("bad query")));
        Assert.False(ReadStore.IsUnavailable(new NpgsqlException("syntax error")));
    }
}
