using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DisputePortal.Api.Events;

/// <summary>
/// Degraded, never Unhealthy, when events are not getting out: customers can still raise and
/// withdraw disputes (the outbox holds the events), but the back office is not hearing about them.
/// </summary>
public sealed class OutboxHealthCheck : IHealthCheck
{
    public static readonly TimeSpan MaxPendingAge = TimeSpan.FromSeconds(60);

    private readonly IServiceScopeFactory _scopes;

    public OutboxHealthCheck(IServiceScopeFactory scopes) => _scopes = scopes;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var age = await scope.ServiceProvider.GetRequiredService<OutboxRelay>().OldestPendingAgeAsync(ct);

        return age is { } pending && pending > MaxPendingAge
            ? HealthCheckResult.Degraded($"Oldest unpublished event is {pending.TotalSeconds:0} s old.")
            : HealthCheckResult.Healthy();
    }
}
