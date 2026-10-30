using System.Net;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using DisputePortal.Api.DTOs;
using DisputePortal.Api.Events;
using DisputePortal.Api.Models;
using DisputePortal.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DisputePortal.IntegrationTests;

/// <summary>End to end through a real broker: the outbox publisher and the decision consumer.</summary>
[Collection(ApiCollection.Name)]
public class KafkaTests : IClassFixture<KafkaFixture>, IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly ApiFactory _api;
    private readonly KafkaFixture _kafka;

    public KafkaTests(ContainersFixture db, KafkaFixture kafka)
    {
        _kafka = kafka;
        _api = new ApiFactory(db, kafkaBootstrapServers: kafka.BootstrapServers);
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

    private async Task WaitForStatusAsync(Guid disputeId, DisputeStatus status)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            var current = await _api.WithDbAsync(db => db.Disputes.AsNoTracking().Where(d => d.Id == disputeId).Select(d => d.Status).SingleAsync());
            if (current == status) return;
            await Task.Delay(250);
        }
        throw new TimeoutException($"Dispute {disputeId} did not reach {status} within {Timeout}.");
    }
}
