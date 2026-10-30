using DisputePortal.Api.Data;
using DisputePortal.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace DisputePortal.Api.Events;

/// <param name="Published">Messages acknowledged by the broker in this pass.</param>
/// <param name="Failed">A publish failed; the rest of the batch waits for the next pass.</param>
public sealed record OutboxPass(int Published, bool Failed);

/// <summary>
/// One pass over the outbox: claim the oldest unpublished rows, publish them in order, mark them
/// published. Run in a loop by <see cref="OutboxPublisherService"/>.
/// </summary>
public sealed class OutboxRelay
{
    public const int BatchSize = 50;

    // SKIP LOCKED: with several API instances, each claims different rows instead of queueing behind
    // (or double-publishing) another's batch. A constant: no input reaches this SQL.
    private const string ClaimSql =
        "SELECT * FROM \"OutboxMessages\" WHERE \"PublishedAt\" IS NULL ORDER BY \"Id\" LIMIT 50 FOR UPDATE SKIP LOCKED";

    private readonly AppDbContext _db;
    private readonly IEventPublisher _publisher;
    private readonly TimeProvider _clock;
    private readonly ILogger<OutboxRelay> _logger;

    public OutboxRelay(AppDbContext db, IEventPublisher publisher, TimeProvider clock, ILogger<OutboxRelay> logger)
    {
        _db = db;
        _publisher = publisher;
        _clock = clock;
        _logger = logger;
    }

    public Task<OutboxPass> PublishPendingAsync(CancellationToken ct) =>
        // An explicit transaction under a retrying execution strategy must run inside it, so a
        // transient database error replays the whole pass rather than half of it.
        _db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            var batch = await _db.OutboxMessages.FromSqlRaw(ClaimSql).ToListAsync(ct);
            var published = 0;
            var failed = false;

            foreach (var message in batch)
            {
                try
                {
                    await _publisher.PublishAsync(message.Topic, message.Key, message.Payload,
                        new Dictionary<string, string> { ["event-type"] = message.EventType, ["event-id"] = message.EventId.ToString() }, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Stop here rather than skip ahead: a later row may be the same dispute's next
                    // event, and publishing it first would put the two out of order.
                    message.Attempts++;
                    message.LastError = Truncate($"{ex.GetType().Name}: {ex.Message}", 500);
                    failed = true;
                    _logger.LogWarning("Outbox: publishing {EventType} {EventId} for dispute {DisputeKey} to {Topic} failed (attempt {Attempts}): {Error}",
                        message.EventType, message.EventId, message.Key, message.Topic, message.Attempts, ex.GetType().Name);
                    break;
                }

                message.PublishedAt = _clock.GetUtcNow().UtcDateTime;
                published++;
                _logger.LogInformation("Published {EventType} {EventId} for dispute {DisputeKey} to {Topic}",
                    message.EventType, message.EventId, message.Key, message.Topic);
            }

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new OutboxPass(published, failed);
        });

    /// <summary>Age of the oldest unpublished event, or null when the outbox is empty.</summary>
    public async Task<TimeSpan?> OldestPendingAgeAsync(CancellationToken ct)
    {
        var oldest = await _db.OutboxMessages.AsNoTracking()
            .Where(m => m.PublishedAt == null)
            .OrderBy(m => m.Id)
            .Select(m => (DateTime?)m.OccurredAt)
            .FirstOrDefaultAsync(ct);
        return oldest is null ? null : _clock.GetUtcNow().UtcDateTime - oldest.Value;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
