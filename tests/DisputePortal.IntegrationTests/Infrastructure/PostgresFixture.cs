using Testcontainers.PostgreSql;

namespace DisputePortal.IntegrationTests.Infrastructure;

/// <summary>
/// One real PostgreSQL container for the whole test run. A real database rather than EF's
/// in-memory provider, because the rules worth testing live in Postgres: the partial unique
/// index on open disputes, the xmin concurrency token, the dispute-number sequence and ILIKE search.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("disputes_test")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "api";
}
