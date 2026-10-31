using DisputePortal.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace DisputePortal.IntegrationTests.Infrastructure;

/// <summary>
/// Runs an action once, just before the next save that matches. That puts a competing write (or a
/// failure) exactly between a service's read and its write, so a race is certain, not a matter of
/// timing. Register it with <see cref="AddTo"/> through <c>ApiFactory(configureServices:)</c>.
/// </summary>
public sealed class BeforeSave : SaveChangesInterceptor
{
    private (Func<EntityEntry, bool> When, Func<Task> Action)? _armed;

    /// <summary>Waits for a save holding an entry that matches <paramref name="when"/>. Fires once.</summary>
    public void Arm(Func<EntityEntry, bool> when, Func<Task> action) => _armed = (when, action);

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (_armed is { } armed && eventData.Context!.ChangeTracker.Entries().Any(armed.When))
        {
            // Disarmed first: the competing write saves too, and must not set this off again.
            _armed = null;
            await armed.Action();
        }

        return result;
    }

    public void AddTo(IServiceCollection services)
    {
        var registered = services.Single(s => s.ServiceType == typeof(DbContextOptions<AppDbContext>));
        services.Remove(registered);
        services.Add(new ServiceDescriptor(
            typeof(DbContextOptions<AppDbContext>),
            provider =>
            {
                var options = (DbContextOptions<AppDbContext>)registered.ImplementationFactory!(provider);
                return new DbContextOptionsBuilder<AppDbContext>(options).AddInterceptors(this).Options;
            },
            registered.Lifetime));
    }
}
