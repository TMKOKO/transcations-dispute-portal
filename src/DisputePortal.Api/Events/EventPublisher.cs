using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.Options;

namespace DisputePortal.Api.Events;

public interface IEventPublisher
{
    /// <summary>Returns once the broker has acknowledged the message; throws if it has not.</summary>
    Task PublishAsync(string topic, string key, string payload, IReadOnlyDictionary<string, string> headers, CancellationToken ct);
}

/// <summary>One producer for the process (librdkafka batches and reconnects on its own).</summary>
public sealed class KafkaEventPublisher : IEventPublisher, IDisposable
{
    private readonly IProducer<string, string> _producer;

    public KafkaEventPublisher(IOptions<KafkaOptions> options)
    {
        _producer = new ProducerBuilder<string, string>(new ProducerConfig
        {
            BootstrapServers = options.Value.BootstrapServers,
            ClientId = "dispute-portal-api",
            // Acks from every in-sync replica, and no duplicates or reordering from the producer's
            // own retries. Duplicates from re-running the outbox are still possible; consumers dedupe.
            Acks = Acks.All,
            EnableIdempotence = true,
            // Fail a send within 10 s rather than librdkafka's default 5 minutes: OutboxRelay holds
            // row locks while it publishes, and will try again on its next pass anyway.
            MessageTimeoutMs = 10_000,
        }).Build();
    }

    public async Task PublishAsync(string topic, string key, string payload, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        var message = new Message<string, string> { Key = key, Value = payload, Headers = [] };
        foreach (var (name, value) in headers)
            message.Headers.Add(name, Encoding.UTF8.GetBytes(value));

        await _producer.ProduceAsync(topic, message, ct);
    }

    public void Dispose()
    {
        // Give anything in flight a moment to be acknowledged on shutdown.
        _producer.Flush(TimeSpan.FromSeconds(5));
        _producer.Dispose();
    }
}
