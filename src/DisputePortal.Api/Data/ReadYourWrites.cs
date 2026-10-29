using StackExchange.Redis;

namespace DisputePortal.Api.Data;

/// <summary>
/// Read-your-writes over a lagging replica. After a customer changes something, their reads go to
/// the primary for a few seconds, so the dispute they just raised is in the list they land on even
/// if the replica has not replayed it yet. Kept in Redis, not in memory, so every API instance sees it.
/// </summary>
public sealed class ReadYourWrites
{
    /// <summary>Far longer than replica lag should ever be; short enough that the primary carries little extra.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(5);

    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<ReadYourWrites> _logger;

    public ReadYourWrites(IConnectionMultiplexer redis, ILogger<ReadYourWrites> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    internal static string KeyFor(Guid customerId) => $"rw:{customerId:N}";

    /// <summary>Call after a write commits. Never throws: the write has already happened.</summary>
    public async Task MarkAsync(Guid customerId)
    {
        try
        {
            await _redis.GetDatabase().StringSetAsync(KeyFor(customerId), "1", Window);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            // Worst case the customer's next read comes from the replica and may be a moment behind.
            _logger.LogWarning("Read-your-writes: could not mark customer {CustomerId}: {Error}", customerId, ex.GetType().Name);
        }
    }

    /// <summary>True when the customer wrote within <see cref="Window"/>, or when Redis cannot say (the primary is always safe).</summary>
    public async Task<bool> IsRecentAsync(Guid customerId)
    {
        try
        {
            return await _redis.GetDatabase().KeyExistsAsync(KeyFor(customerId));
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            return true;
        }
    }
}
