using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using DisputePortal.Api.DTOs;
using DisputePortal.Api.Events;
using DisputePortal.Api.Models;
using DisputePortal.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DisputePortal.IntegrationTests;

/// <summary>End to end through a real broker: the outbox publisher and the decision consumer.</summary>
[Collection(ApiCollection.Name)]
public class KafkaTests : IClassFixture<KafkaFixture>, IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly ContainersFixture _containers;
    private readonly ApiFactory _api;
    private readonly KafkaFixture _kafka;

    public KafkaTests(ContainersFixture db, KafkaFixture kafka)
    {
        _containers = db;
        _kafka = kafka;
        _api = new ApiFactory(db, kafkaBootstrapServers: kafka.BootstrapServers, agentEnabled: true);
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _api.DisposeAsync().AsTask();

    [Fact]
    public async Task A_raised_dispute_reaches_Kafka_and_back_office_decisions_come_back_to_its_timeline()
    {
        var customer = await _api.CreateCustomerAsync();
        var tx = await _api.CreateTransactionAsync(customer.Id);
        var client = await _api.CustomerClientAsync(customer);

        var raised = await EventsTests.RaiseAsync(client, tx.Id);
        Assert.Equal(HttpStatusCode.Created, raised.StatusCode);
        var disputeId = (await raised.ReadAsync<DisputeDetailResponse>()).Id;

        // 1. DisputeSubmitted arrives on its topic, keyed by the dispute.
        var submitted = Consume(DisputeTopics.Submitted, disputeId.ToString());
        Assert.Equal(DisputeEventTypes.DisputeSubmitted, Encoding.UTF8.GetString(submitted.Headers.GetLastBytes("event-type")));
        var payload = JsonDocument.Parse(submitted.Value).RootElement;
        Assert.Equal(DisputeEventTypes.DisputeSubmitted, payload.GetProperty("eventType").GetString());
        Assert.False(payload.TryGetProperty("description", out _));

        // 2. The back office decides; the API applies it.
        using var producer = new ProducerBuilder<string, string>(new ProducerConfig { BootstrapServers = _kafka.BootstrapServers }).Build();
        await producer.ProduceAsync(DisputeTopics.UnderReview, new Message<string, string>
        {
            Key = disputeId.ToString(),
            Value = new DisputeEvent
            {
                EventId = Guid.NewGuid(),
                EventType = DisputeEventTypes.UnderReview,
                OccurredAt = DateTimeOffset.UtcNow,
                DisputeId = disputeId,
                Note = "Looking into it.",
            }.ToJson(),
        });

        await WaitForStatusAsync(disputeId, DisputeStatus.UnderReview);

        // 3. A message that cannot apply is parked on the DLQ, not retried for ever.
        await producer.ProduceAsync(DisputeTopics.Rejected, new Message<string, string> { Key = disputeId.ToString(), Value = "not json" });
        var parked = Consume(DisputeTopics.DecisionsDeadLetter, disputeId.ToString());
        Assert.Equal("malformed_event", Encoding.UTF8.GetString(parked.Headers.GetLastBytes("dlq-reason")));
        Assert.Equal(DisputeTopics.Rejected, Encoding.UTF8.GetString(parked.Headers.GetLastBytes("source-topic")));
    }

    [Fact]
    public async Task An_agents_decisions_go_through_the_broker_and_come_back_to_the_customers_timeline()
    {
        var customer = await _api.CreateCustomerAsync();
        var tx = await _api.CreateTransactionAsync(customer.Id);
        var client = await _api.CustomerClientAsync(customer);
        var agent = await _api.AgentClientAsync(await _api.CreateAgentAsync());

        var raised = await EventsTests.RaiseAsync(client, tx.Id);
        var disputeId = (await raised.ReadAsync<DisputeDetailResponse>()).Id;

        // 1. Too early: published all the same, and the consumer parks it on the DLQ.
        var early = await agent.PostAsJsonAsync("/api/agent/disputes/decision", new { disputeId, decision = "RefundIssued" });
        Assert.Equal(HttpStatusCode.Accepted, early.StatusCode);
        Assert.Equal(disputeId.ToString(), Consume(DisputeTopics.RefundIssued, disputeId.ToString()).Key);
        var parked = Consume(DisputeTopics.DecisionsDeadLetter, disputeId.ToString());
        Assert.Equal("transition_not_allowed", Encoding.UTF8.GetString(parked.Headers.GetLastBytes("dlq-reason")));

        // 2. A move that is allowed reaches the customer's timeline as the agent's.
        var valid = await agent.PostAsJsonAsync("/api/agent/disputes/decision", new { disputeId, decision = "UnderReview", note = "Looking into it." });
        Assert.Equal(HttpStatusCode.Accepted, valid.StatusCode);
        await WaitForStatusAsync(disputeId, DisputeStatus.UnderReview);

        var detail = await (await client.PostAsJsonAsync("/api/disputes/lookup", new { disputeId })).ReadAsync<DisputeDetailResponse>();
        var entry = detail.Timeline[^1];
        Assert.Equal(DisputeActor.Agent, entry.ChangedBy);
        Assert.Equal("Looking into it.", entry.Note);
    }

    [Fact]
    public async Task A_decision_that_fails_is_delivered_again_and_applied_once()
    {
        var beforeSave = new BeforeSave();
        await using var api = new ApiFactory(_containers, kafkaBootstrapServers: _kafka.BootstrapServers, configureServices: beforeSave.AddTo);
        var customer = await api.CreateCustomerAsync();
        var tx = await api.CreateTransactionAsync(customer.Id);
        var client = await api.CustomerClientAsync(customer);
        var disputeId = (await (await EventsTests.RaiseAsync(client, tx.Id)).ReadAsync<DisputeDetailResponse>()).Id;

        // The first attempt fails after the handler has decided, as if the database dropped out.
        beforeSave.Arm(e => e.Entity is InboxMessage m && m.DisputeId == disputeId,
            () => throw new InvalidOperationException("database unavailable"));

        using var producer = new ProducerBuilder<string, string>(new ProducerConfig { BootstrapServers = _kafka.BootstrapServers }).Build();
        await producer.ProduceAsync(DisputeTopics.UnderReview, new Message<string, string>
        {
            Key = disputeId.ToString(),
            Value = new DisputeEvent
            {
                EventId = Guid.NewGuid(),
                EventType = DisputeEventTypes.UnderReview,
                OccurredAt = DateTimeOffset.UtcNow,
                DisputeId = disputeId,
            }.ToJson(),
        });

        await WaitForStatusAsync(api, disputeId, DisputeStatus.UnderReview);

        Assert.Contains(api.Logs.GetSnapshot(), r => r.Message.Contains("failed (InvalidOperationException); retrying"));
        Assert.Equal(1, await api.WithDbAsync(db => db.InboxMessages.CountAsync(i => i.DisputeId == disputeId)));
        Assert.Equal(1, await api.WithDbAsync(db => db.Set<DisputeStatusHistory>().CountAsync(
            h => h.DisputeId == disputeId && h.ToStatus == DisputeStatus.UnderReview)));
    }

    [Fact]
    public async Task With_the_publisher_down_requests_still_succeed_health_degrades_and_events_go_out_on_recovery()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var publisher = new ToggledPublisher();
        await using var api = new ApiFactory(_containers, clock: clock, kafkaBootstrapServers: _kafka.BootstrapServers,
            configureServices: s => s.AddSingleton<IEventPublisher>(publisher));
        var customer = await api.CreateCustomerAsync();
        var tx = await api.CreateTransactionAsync(customer.Id);
        var client = await api.CustomerClientAsync(customer);

        // Other tests leave pending rows in the shared table, so start from an empty outbox.
        await WaitUntilAsync(() => api.WithDbAsync(db => db.OutboxMessages.AllAsync(m => m.PublishedAt != null)), "the outbox to drain");
        await AssertHealthAsync(api, "Healthy");

        publisher.Down = true;
        var raised = await EventsTests.RaiseAsync(client, tx.Id);

        Assert.Equal(HttpStatusCode.Created, raised.StatusCode);
        var key = (await raised.ReadAsync<DisputeDetailResponse>()).Id.ToString();
        await WaitUntilAsync(() => Task.FromResult(publisher.Refused > 0), "the relay to try");
        Assert.Null((await api.WithDbAsync(db => db.OutboxMessages.AsNoTracking().SingleAsync(m => m.Key == key))).PublishedAt);

        // Only an event left waiting too long counts, and the API keeps serving (200, not 503).
        clock.Advance(OutboxHealthCheck.MaxPendingAge + TimeSpan.FromSeconds(1));
        await AssertHealthAsync(api, "Degraded");

        publisher.Down = false;
        await WaitUntilAsync(() => api.WithDbAsync(db => db.OutboxMessages.AnyAsync(m => m.Key == key && m.PublishedAt != null)), "the event to go out");
        Assert.Contains(key, publisher.SentKeys);
        await AssertHealthAsync(api, "Healthy");
    }

    [Fact]
    public async Task When_a_relay_pass_fails_outright_the_service_survives_and_the_event_goes_out()
    {
        var beforeSave = new BeforeSave();
        var publisher = new ToggledPublisher();
        await using var api = new ApiFactory(_containers, kafkaBootstrapServers: _kafka.BootstrapServers, configureServices: s =>
        {
            s.AddSingleton<IEventPublisher>(publisher);
            beforeSave.AddTo(s);
        });
        var customer = await api.CreateCustomerAsync();
        var tx = await api.CreateTransactionAsync(customer.Id);
        var client = await api.CustomerClientAsync(customer);

        // Marking the batch published is the save that fails, so the messages have already gone once.
        beforeSave.Arm(e => e.Entity is OutboxMessage && e.State == EntityState.Modified,
            () => throw new InvalidOperationException("database unavailable"));

        var raised = await EventsTests.RaiseAsync(client, tx.Id);
        var key = (await raised.ReadAsync<DisputeDetailResponse>()).Id.ToString();

        await WaitUntilAsync(() => api.WithDbAsync(db => db.OutboxMessages.AnyAsync(m => m.Key == key && m.PublishedAt != null)), "the event to go out");
        Assert.Contains(api.Logs.GetSnapshot(), r => r.Message.Contains("Outbox: pass failed (InvalidOperationException)"));
    }

    private static async Task AssertHealthAsync(ApiFactory api, string expected)
    {
        var response = await api.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, await response.Content.ReadAsStringAsync());
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(100);
        }
        throw new TimeoutException($"Waited {Timeout} for {what}.");
    }

    private sealed class ToggledPublisher : IEventPublisher
    {
        private int _refused;

        public volatile bool Down;
        public int Refused => Volatile.Read(ref _refused);
        public ConcurrentBag<string> SentKeys { get; } = [];

        public Task PublishAsync(string topic, string key, string payload, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
        {
            if (Down)
            {
                Interlocked.Increment(ref _refused);
                throw new InvalidOperationException("broker unreachable");
            }
            SentKeys.Add(key);
            return Task.CompletedTask;
        }
    }

    private Message<string, string> Consume(string topic, string key)
    {
        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = _kafka.BootstrapServers,
            GroupId = $"test-{Guid.NewGuid():N}",
            AutoOffsetReset = AutoOffsetReset.Earliest,
        }).Build();
        consumer.Subscribe(topic);

        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            var result = consumer.Consume(TimeSpan.FromSeconds(1));
            if (result?.Message.Key == key) return result.Message;
        }
        throw new TimeoutException($"No message keyed {key} on {topic} within {Timeout}.");
    }

    private Task WaitForStatusAsync(Guid disputeId, DisputeStatus status) => WaitForStatusAsync(_api, disputeId, status);

    private static async Task WaitForStatusAsync(ApiFactory api, Guid disputeId, DisputeStatus status)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            var current = await api.WithDbAsync(db => db.Disputes.AsNoTracking().Where(d => d.Id == disputeId).Select(d => d.Status).SingleAsync());
            if (current == status) return;
            await Task.Delay(250);
        }
        throw new TimeoutException($"Dispute {disputeId} did not reach {status} within {Timeout}.");
    }
}
