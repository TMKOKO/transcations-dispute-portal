using Microsoft.EntityFrameworkCore;

namespace DisputePortal.Api.Data;

/// <summary>
/// The same model as <see cref="AppDbContext"/>, on <c>ConnectionStrings:ReadOnlyConnection</c> (a
/// streaming replica), or on the primary when that is not set. Customer-facing reads go here through
/// <see cref="ReadStore"/>; anything that decides a write stays on AppDbContext, because a replica
/// can lag. Migrations belong to AppDbContext only.
/// </summary>
public sealed class ReadDbContext : AppDbContext
{
    public ReadDbContext(DbContextOptions<ReadDbContext> options) : base(options) { }

    public override int SaveChanges(bool acceptAllChangesOnSuccess) => throw ReadOnly();

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default) =>
        throw ReadOnly();

    private static InvalidOperationException ReadOnly() =>
        new("ReadDbContext is read-only. Write through AppDbContext (DisputeService).");
}
