using Confluent.Kafka;
using Microsoft.Extensions.Options;

namespace DisputePortal.Api.Events;

/// <summary>
/// Reads the back office's decision topics and hands each message to <see cref="DecisionHandler"/>.
/// <list type="bullet">
/// <item>The offset is committed only after the handler has committed its database change, so a
/// crash in between redelivers the message, and the inbox makes the second delivery a no-op.</item>
/// <item>A message that can never apply goes to <see cref="DisputeTopics.DecisionsDeadLetter"/> and
/// is committed, so it cannot block the dispute events behind it.</item>
/// <item>Anything else (database down) seeks back to the same message and retries with backoff:
/// nothing is skipped.</item>
/// </list>
/// </summary>
public sealed class DecisionConsumer : BackgroundService
{
    private static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopes;
    private readonly IEventPublisher _publisher;
    private readonly KafkaOptions _options;
    private readonly ILogger<DecisionConsumer> _logger;

    public DecisionConsumer(IServiceScopeFactory scopes, IEventPublisher publisher, IOptions<KafkaOptions> options,
        ILogger<DecisionConsumer> logger)
    {
        _scopes = scopes;
        _publisher = publisher;
        _options = options.Value;
        _logger = logger;
    }

    // Consume() blocks its thread, so run the loop off the host's startup path.
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Factory.StartNew(() => RunAsync(stoppingToken), stoppingToken, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = _options.BootstrapServers,
            GroupId = _options.GroupId,
            ClientId = "dispute-portal-api",
            EnableAutoCommit = false,
            // A new group starts from the beginning, so no decision made before first start-up is missed.
            AutoOffsetReset = AutoOffsetReset.Earliest,
        }).Build();

        consumer.Subscribe(DisputeTopics.Decisions);
        _logger.LogInformation("Decision consumer subscribed to {Topics} as group {GroupId}",
            string.Join(", ", DisputeTopics.Decisions), _options.GroupId);

        var backoff = FirstBackoff;
        while (!stoppingToken.IsCancellationRequested)
        {
            ConsumeResult<string, string> message;
            try
            {
                message = consumer.Consume(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ConsumeException ex)
            {
                // The broker is unreachable or a topic is missing; librdkafka keeps reconnecting.
                _logger.LogWarning("Decision consumer: consume failed ({Code}); retrying in {Delay}", ex.Error.Code, backoff);
                if (!await DelayAsync(backoff, stoppingToken)) break;
                backoff = Next(backoff);
                continue;
            }

            try
            {
                using (var scope = _scopes.CreateScope())
                {
                    var result = await scope.ServiceProvider.GetRequiredService<DecisionHandler>()
                        .HandleAsync(message.Topic, message.Message.Value ?? string.Empty, stoppingToken);

                    if (result.Outcome == DecisionOutcome.DeadLetter)
                        await DeadLetterAsync(message, result.Reason!, stoppingToken);
                }

                consumer.Commit(message);
                backoff = FirstBackoff;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Decision consumer: {Topic} partition {Partition} offset {Offset} failed ({Error}); retrying in {Delay}",
                    message.Topic, message.Partition.Value, message.Offset.Value, ex.GetType().Name, backoff);
                // Rewind so the next Consume() returns this same message.
                consumer.Seek(message.TopicPartitionOffset);
                if (!await DelayAsync(backoff, stoppingToken)) break;
                backoff = Next(backoff);
            }
        }

        consumer.Close();
    }

    private async Task DeadLetterAsync(ConsumeResult<string, string> message, string reason, CancellationToken ct)
    {
        // The original bytes, untouched, plus where they came from and why they were parked.
        await _publisher.PublishAsync(DisputeTopics.DecisionsDeadLetter, message.Message.Key ?? string.Empty,
            message.Message.Value ?? string.Empty,
            new Dictionary<string, string>
            {
                ["dlq-reason"] = reason,
                ["source-topic"] = message.Topic,
                ["source-partition"] = message.Partition.Value.ToString(),
                ["source-offset"] = message.Offset.Value.ToString(),
            }, ct);

        _logger.LogWarning("Sent {Topic} partition {Partition} offset {Offset} to {DeadLetterTopic}: {Reason}",
            message.Topic, message.Partition.Value, message.Offset.Value, DisputeTopics.DecisionsDeadLetter, reason);
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); return true; }
        catch (OperationCanceledException) { return false; }
    }

    private static TimeSpan Next(TimeSpan backoff) => backoff * 2 < MaxBackoff ? backoff * 2 : MaxBackoff;
}
