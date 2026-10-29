using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace DisputePortal.Api.Auth;

/// <summary>Without Redis nobody can sign in or stay signed in, so /health reports it.</summary>
public sealed class RedisHealthCheck : IHealthCheck
{
    private readonly IConnectionMultiplexer _redis;

    public RedisHealthCheck(IConnectionMultiplexer redis) => _redis = redis;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await _redis.GetDatabase().PingAsync();
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex) when (ex is RedisConnectionException or RedisTimeoutException)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "Redis is unreachable.");
        }
    }
}
