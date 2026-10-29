using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DisputePortal.Api.Data;
using DisputePortal.Api.DTOs;
using DisputePortal.Api.Events;
using DisputePortal.Api.Models;
using DisputePortal.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DisputePortal.IntegrationTests;

/// <summary>Outbox rows, the relay and the decision handler, without a broker (Kafka is off).</summary>
[Collection(ApiCollection.Name)]
public class EventsTests : IAsyncLifetime
{
    private const string Description = "I was charged twice for a single purchase.";

    private readonly ApiFactory _api;

    public EventsTests(ContainersFixture db) => _api = new ApiFactory(db);

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _api.DisposeAsync().AsTask();

    internal static async Task<HttpResponseMessage> RaiseAsync(HttpClient client, Guid transactionId, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/disputes")
        {
            Content = JsonContent.Create(new { transactionId, reason = "DuplicateCharge", description = Description, disputedAmount = 100m }),
        };
        request.Headers.Add("Idempotency-Key", key ?? $"key-{Guid.NewGuid():N}");
        return await client.SendAsync(request);
    }

    private async Task<(HttpClient Client, Guid DisputeId)> RaisedDisputeAsync()
    {
        var customer = await _api.CreateCustomerAsync();
        var tx = await _api.CreateTransactionAsync(customer.Id);
        var client = await _api.CustomerClientAsync(customer);
        var response = await RaiseAsync(client, tx.Id);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (client, (await response.ReadAsync<DisputeDetailResponse>()).Id);
    }

    private Task<List<OutboxMessage>> OutboxForAsync(Guid disputeId) => _api.WithDbAsync(db =>
        db.OutboxMessages.AsNoTracking().Where(m => m.Key == disputeId.ToString()).OrderBy(m => m.Id).ToListAsync());

    private Task<DecisionResult> HandleAsync(string topic, string payload) =>
        _api.WithServiceAsync<DecisionHandler, DecisionResult>(h => h.HandleAsync(topic, payload, CancellationToken.None));

    private static string Decision(Guid disputeId, string eventType, string? note = null, Guid? eventId = null) =>
        new DisputeEvent
        {
            EventId    = eventId ?? Guid.NewGuid(),
            EventType  = eventType,
            OccurredAt = DateTimeOffset.UtcNow,
            DisputeId  = disputeId,
            Note       = note,
        }.ToJson();

    private Task<Dispute> DisputeAsync(Guid id) => _api.WithDbAsync(db =>
        db.Disputes.AsNoTracking().Include(d => d.History).SingleAsync(d => d.Id == id));

    // --- Outbox ---

    [Fact]
    public async Task Raising_a_dispute_writes_one_DisputeSubmitted_event_without_the_description()
    {
        var (_, disputeId) = await RaisedDisputeAsync();

        var message = Assert.Single(await OutboxForAsync(disputeId));
        Assert.Equal(DisputeTopics.Submitted, message.Topic);
        Assert.Equal(DisputeEventTypes.DisputeSubmitted, message.EventType);
        Assert.Null(message.PublishedAt);

        var payload = JsonDocument.Parse(message.Payload).RootElement;
        Assert.Equal(disputeId, payload.GetProperty("disputeId").GetGuid());
        Assert.Equal(100m, payload.GetProperty("amount").GetDecimal());
        Assert.Equal("DuplicateCharge", payload.GetProperty("reason").GetString());
        Assert.False(payload.TryGetProperty("description", out _));
        Assert.DoesNotContain(Description, message.Payload);
    }

    [Fact]
    public async Task A_replayed_or_refused_raise_writes_no_event()
    {
        var customer = await _api.CreateCustomerAsync();
        var tx = await _api.CreateTransactionAsync(customer.Id);
        var client = await _api.CustomerClientAsync(customer);

        var first = await RaiseAsync(client, tx.Id, key: "same-key");
        var replay = await RaiseAsync(client, tx.Id, key: "same-key");
        var conflict = await RaiseAsync(client, tx.Id);   // a second open dispute on the transaction: 409

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);   // replayed
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        // Only one dispute exists on the transaction, so its events are all the raises produced.
        var disputeId = (await first.ReadAsync<DisputeDetailResponse>()).Id;
        Assert.Single(await OutboxForAsync(disputeId));
        Assert.Equal(1, await _api.WithDbAsync(db => db.Disputes.CountAsync(d => d.TransactionId == tx.Id)));
    }

    [Fact]
    public async Task Withdrawing_writes_DisputeWithdrawn_after_DisputeSubmitted()
    {
        var (client, disputeId) = await RaisedDisputeAsync();

        var response = await client.PostAsJsonAsync("/api/disputes/withdraw", new { disputeId, note = "Merchant refunded me." });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var events = await OutboxForAsync(disputeId);
        Assert.Equal([DisputeEventTypes.DisputeSubmitted, DisputeEventTypes.DisputeWithdrawn], events.Select(m => m.EventType));
        Assert.Equal(DisputeTopics.Withdrawn, events[1].Topic);
        // The withdraw note is the customer's free text: it stays in the database.
        Assert.DoesNotContain("Merchant refunded me.", events[1].Payload);
    }

    [Fact]
    public async Task The_relay_publishes_pending_events_in_order_and_marks_them()
    {
        var (client, disputeId) = await RaisedDisputeAsync();
        await client.PostAsJsonAsync("/api/disputes/withdraw", new { disputeId });

        var publisher = new RecordingPublisher();
        await DrainAsync(publisher);

        var ours = publisher.Sent.Where(s => s.Key == disputeId.ToString()).ToList();
        Assert.Equal([DisputeTopics.Submitted, DisputeTopics.Withdrawn], ours.Select(s => s.Topic));
        Assert.Equal(DisputeEventTypes.DisputeSubmitted, ours[0].Headers["event-type"]);
        Assert.All(await OutboxForAsync(disputeId), m => Assert.NotNull(m.PublishedAt));
    }

    [Fact]
    public async Task When_the_broker_fails_events_stay_pending_and_the_attempt_is_recorded()
    {
        var (_, disputeId) = await RaisedDisputeAsync();
        await DrainAsync(new RecordingPublisher());   // everything before this test's next event is out
        var (_, nextDisputeId) = await RaisedDisputeAsync();

        var pass = await RelayAsync(new RecordingPublisher { Fail = true });

        Assert.True(pass.Failed);
        Assert.Equal(0, pass.Published);
        var message = Assert.Single(await OutboxForAsync(nextDisputeId));
        Assert.Null(message.PublishedAt);
        Assert.Equal(1, message.Attempts);
        Assert.Contains("broker unreachable", message.LastError);
        Assert.NotNull(Assert.Single(await OutboxForAsync(disputeId)).PublishedAt);

        await DrainAsync(new RecordingPublisher());   // the broker is back: it goes out
        Assert.NotNull(Assert.Single(await OutboxForAsync(nextDisputeId)).PublishedAt);
    }

    private Task<OutboxPass> RelayAsync(IEventPublisher publisher) => _api.WithDbAsync(db =>
        new OutboxRelay(db, publisher, TimeProvider.System, _api.Services.GetRequiredService<ILogger<OutboxRelay>>())
            .PublishPendingAsync(CancellationToken.None));

    // Other tests share the outbox table, so keep going until a pass comes back short.
    private async Task DrainAsync(IEventPublisher publisher)
    {
        while ((await RelayAsync(publisher)).Published == OutboxRelay.BatchSize) { }
    }

    // --- Back-office decisions ---

    [Fact]
    public async Task UnderReview_moves_the_dispute_and_adds_an_agent_timeline_row()
    {
        var (_, disputeId) = await RaisedDisputeAsync();

        var result = await HandleAsync(DisputeTopics.UnderReview, Decision(disputeId, DisputeEventTypes.UnderReview, "We have asked the merchant."));

        Assert.Equal(DecisionOutcome.Applied, result.Outcome);
        var dispute = await DisputeAsync(disputeId);
        Assert.Equal(DisputeStatus.UnderReview, dispute.Status);
        var row = dispute.History.OrderBy(h => h.ChangedAt).Last();
        Assert.Equal(DisputeActor.Agent, row.ChangedBy);
        Assert.Equal("We have asked the merchant.", row.Note);
    }

    [Fact]
    public async Task RefundIssued_after_UnderReview_resolves_the_dispute()
    {
        var (_, disputeId) = await RaisedDisputeAsync();

        await HandleAsync(DisputeTopics.UnderReview, Decision(disputeId, DisputeEventTypes.UnderReview));
        var result = await HandleAsync(DisputeTopics.RefundIssued, Decision(disputeId, DisputeEventTypes.RefundIssued, "Refunded R100.00."));

        Assert.Equal(DecisionOutcome.Applied, result.Outcome);
        var dispute = await DisputeAsync(disputeId);
        Assert.Equal(DisputeStatus.Resolved, dispute.Status);
        Assert.NotNull(dispute.ResolvedAt);
    }

    [Fact]
    public async Task The_same_event_delivered_twice_is_applied_once()
    {
        var (_, disputeId) = await RaisedDisputeAsync();
        var payload = Decision(disputeId, DisputeEventTypes.Rejected, "Card was present.");

        var first = await HandleAsync(DisputeTopics.Rejected, payload);
        var second = await HandleAsync(DisputeTopics.Rejected, payload);

        Assert.Equal(DecisionOutcome.Applied, first.Outcome);
        Assert.Equal(DecisionOutcome.Duplicate, second.Outcome);
        Assert.Equal(1, (await DisputeAsync(disputeId)).History.Count(h => h.ToStatus == DisputeStatus.Rejected));
    }

    [Fact]
    public async Task A_decision_on_a_withdrawn_dispute_is_stale_and_changes_nothing()
    {
        var (client, disputeId) = await RaisedDisputeAsync();
        await client.PostAsJsonAsync("/api/disputes/withdraw", new { disputeId });

        var result = await HandleAsync(DisputeTopics.UnderReview, Decision(disputeId, DisputeEventTypes.UnderReview));

        Assert.Equal(DecisionOutcome.Stale, result.Outcome);
        Assert.Equal(DisputeStatus.Withdrawn, (await DisputeAsync(disputeId)).Status);
        // Recorded, so a redelivery is a duplicate rather than another look.
        Assert.True(await _api.WithDbAsync(db => db.InboxMessages.AnyAsync(i => i.DisputeId == disputeId && i.Outcome == InboxOutcome.Stale)));
    }

    [Fact]
    public async Task RefundIssued_before_UnderReview_is_dead_lettered()
    {
        var (_, disputeId) = await RaisedDisputeAsync();

        var result = await HandleAsync(DisputeTopics.RefundIssued, Decision(disputeId, DisputeEventTypes.RefundIssued));

        Assert.Equal(DecisionOutcome.DeadLetter, result.Outcome);
        Assert.Equal("transition_not_allowed", result.Reason);
        Assert.Equal(DisputeStatus.Submitted, (await DisputeAsync(disputeId)).Status);
    }

    [Theory]
    [InlineData("not json", "malformed_event")]
    [InlineData("{}", "malformed_event")]
    public async Task A_message_that_is_not_an_event_is_dead_lettered(string payload, string reason)
    {
        var result = await HandleAsync(DisputeTopics.UnderReview, payload);

        Assert.Equal(DecisionOutcome.DeadLetter, result.Outcome);
        Assert.Equal(reason, result.Reason);
    }

    [Fact]
    public async Task Bad_events_are_dead_lettered_with_a_reason()
    {
        var (_, disputeId) = await RaisedDisputeAsync();

        Assert.Equal("event_type_mismatch",
            (await HandleAsync(DisputeTopics.UnderReview, Decision(disputeId, DisputeEventTypes.Rejected))).Reason);
        Assert.Equal("invalid_note",
            (await HandleAsync(DisputeTopics.UnderReview, Decision(disputeId, DisputeEventTypes.UnderReview, "bad‮note"))).Reason);
        Assert.Equal("dispute_not_found",
            (await HandleAsync(DisputeTopics.UnderReview, Decision(Guid.NewGuid(), DisputeEventTypes.UnderReview))).Reason);
        Assert.Equal(DisputeStatus.Submitted, (await DisputeAsync(disputeId)).Status);
    }

    [Fact]
    public async Task A_decision_note_is_never_logged()
    {
        const string note = "Private reason the bank wrote for this customer.";
        var (_, disputeId) = await RaisedDisputeAsync();

        await HandleAsync(DisputeTopics.UnderReview, Decision(disputeId, DisputeEventTypes.UnderReview, note));
        await HandleAsync(DisputeTopics.RefundIssued, Decision(disputeId, DisputeEventTypes.Rejected, note));   // dead-lettered

        Assert.Contains(_api.Logs.GetSnapshot(), r => r.Message.Contains("Applied UnderReview"));
        Assert.DoesNotContain(_api.Logs.GetSnapshot(), r => r.Message.Contains(note));
    }

    private sealed class RecordingPublisher : IEventPublisher
    {
        public bool Fail { get; init; }
        public List<(string Topic, string Key, string Payload, IReadOnlyDictionary<string, string> Headers)> Sent { get; } = [];

        public Task PublishAsync(string topic, string key, string payload, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
        {
            if (Fail) throw new InvalidOperationException("broker unreachable");
            Sent.Add((topic, key, payload, headers));
            return Task.CompletedTask;
        }
    }
}
