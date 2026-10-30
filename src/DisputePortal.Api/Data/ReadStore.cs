using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace DisputePortal.Api.Data;

/// <summary>
/// Where ViewModel reads run. With a replica configured:
/// <list type="bullet">
/// <item>reads go to the replica, which takes load off the primary that every dispute write needs;</item>
/// <item>a customer who wrote in the last few seconds reads from the primary (<see cref="ReadYourWrites"/>);</item>
/// <item>if the replica cannot be reached the read runs once more on the primary, so a replica
/// outage costs latency, not an error page.</item>
/// </list>
/// Without one, everything reads the primary. Queries must be pure reads (AsNoTracking).
/// </summary>
public sealed class ReadStore
{
    public const string ConnectionName = "ReadOnlyConnection";

    private readonly AppDbContext _primary;
    private readonly ReadDbContext _replica;
    private readonly ReadYourWrites _readYourWrites;
    private readonly bool _hasReplica;
    private readonly ILogger<ReadStore> _logger;

    public ReadStore(AppDbContext primary, ReadDbContext replica, ReadYourWrites readYourWrites,
        IConfiguration configuration, ILogger<ReadStore> logger)
    {
        _primary = primary;
        _replica = replica;
        _readYourWrites = readYourWrites;
        _hasReplica = !string.IsNullOrWhiteSpace(configuration.GetConnectionString(ConnectionName));
        _logger = logger;
    }

    /// <param name="customerId">Whose data this is: decides read-your-writes routing.</param>
    /// <param name="operation">A fixed label for logs, e.g. "transactions list". Never caller input.</param>
    /// <param name="query">The read, run against whichever context is chosen; may run twice on failover.</param>
    public async Task<T> QueryAsync<T>(Guid customerId, string operation, Func<AppDbContext, Task<T>> query)
    {
        if (!_hasReplica) return await query(_primary);

        if (await _readYourWrites.IsRecentAsync(customerId))
        {
            _logger.LogInformation("Read {Operation} for customer {CustomerId} pinned to the primary after a recent write",
                operation, customerId);
            return await query(_primary);
        }

        try
        {
            return await query(_replica);
        }
        catch (Exception ex) when (IsUnavailable(ex))
        {
            _logger.LogWarning("Read replica unavailable ({Error}); served {Operation} for customer {CustomerId} from the primary",
                ex.GetType().Name, operation, customerId);
            return await query(_primary);
        }
    }

    /// <summary>
    /// Could not reach or hear back from the database, as opposed to a bad query, which would fail on
    /// the primary too. The replica's retry strategy wraps the last failure in RetryLimitExceededException.
    /// </summary>
    internal static bool IsUnavailable(Exception ex)
    {
        for (var e = (Exception?)ex; e is not null; e = e.InnerException)
        {
            if (e is RetryLimitExceededException or TimeoutException) return true;
            if (e is NpgsqlException { IsTransient: true }) return true;
        }
        return false;
    }
}
