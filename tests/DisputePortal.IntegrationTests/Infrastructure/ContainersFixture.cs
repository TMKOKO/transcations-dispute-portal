using StackExchange.Redis;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace DisputePortal.IntegrationTests.Infrastructure;

/// <summary>
/// One real PostgreSQL and one real Redis container for the whole test run. A real database rather
/// than EF's in-memory provider, because the rules worth testing live in Postgres: the partial
/// unique index on open disputes, the xmin concurrency token, the dispute-number sequence and ILIKE
/// search. A real Redis because session expiry is Redis TTLs (GETEX), which a fake would not prove.
/// </summary>
public sealed class ContainersFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .WithDatabase("disputes_test")
        .Build();

    private readonly RedisContainer _redis = new RedisBuilder()
        .WithImage("redis:7-alpine")
        .Build();

    private ConnectionMultiplexer? _multiplexer;

    public string ConnectionString => _postgres.GetConnectionString();

    public string RedisConnectionString => _redis.GetConnectionString();

    /// <summary>Direct access for assertions on session keys and TTLs.</summary>
    public IDatabase Redis => _multiplexer!.GetDatabase();

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync());
        _multiplexer = await ConnectionMultiplexer.ConnectAsync(RedisConnectionString);
    }

    public async Task DisposeAsync()
    {
        if (_multiplexer is not null) await _multiplexer.DisposeAsync();
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _redis.DisposeAsync().AsTask());
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ContainersFixture>
{
    public const string Name = "api";
}
