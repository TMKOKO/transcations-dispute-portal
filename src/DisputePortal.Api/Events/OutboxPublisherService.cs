namespace DisputePortal.Api.Events;

/// <summary>
/// Drains the outbox to Kafka for as long as the API runs. Kafka being down only delays events:
/// the rows wait, the pass is retried with exponential backoff (1 s up to 30 s), and once the
/// broker is back everything goes out in order.
/// </summary>
public sealed class OutboxPublisherService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<OutboxPublisherService> _logger;

    public OutboxPublisherService(IServiceScopeFactory scopes, ILogger<OutboxPublisherService> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backoff = PollInterval;

        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                using var scope = _scopes.CreateScope();
                var pass = await scope.ServiceProvider.GetRequiredService<OutboxRelay>().PublishPendingAsync(stoppingToken);

                if (pass.Failed)
                {
                    delay = backoff;
                    backoff = Next(backoff);
                }
                else
                {
                    backoff = PollInterval;
                    // A full batch means there is probably more waiting: go again at once.
                    delay = pass.Published == OutboxRelay.BatchSize ? TimeSpan.Zero : PollInterval;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Usually the database. Nothing was marked published, so nothing is lost.
                delay = backoff;
                backoff = Next(backoff);
                _logger.LogWarning("Outbox: pass failed ({Error}); retrying in {Delay}", ex.GetType().Name, delay);
            }

            if (delay > TimeSpan.Zero)
            {
                try { await Task.Delay(delay, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private static TimeSpan Next(TimeSpan backoff) => backoff * 2 < MaxBackoff ? backoff * 2 : MaxBackoff;
}
