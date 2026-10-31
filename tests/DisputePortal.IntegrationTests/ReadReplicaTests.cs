using System.Net;
using DisputePortal.Api.Data;
using DisputePortal.Api.DTOs;
using DisputePortal.IntegrationTests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace DisputePortal.IntegrationTests;

/// <summary>A replica that is down: reads fail over to the primary, and health says Degraded.</summary>
[Collection(ApiCollection.Name)]
public class ReplicaDownTests : IAsyncLifetime
{
    // Nothing listens on port 1, so every connection is refused at once.
    private const string DeadReplica = "Host=127.0.0.1;Port=1;Database=disputes;Username=postgres;Password=postgres";

    private readonly ApiFactory _api;

    public ReplicaDownTests(ContainersFixture db) => _api = new ApiFactory(db, readOnlyConnectionString: DeadReplica);

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _api.DisposeAsync().AsTask();

    [Fact]
    public async Task Reads_are_served_from_the_primary_and_the_failover_is_logged()
    {
        var customer = await _api.CreateCustomerAsync();
        var tx = await _api.CreateTransactionAsync(customer.Id);
        var client = await _api.CustomerClientAsync(customer);

        var list = await client.GetAsync("/api/transactions");
        var me = await client.GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(tx.Id, Assert.Single((await list.ReadAsync<PagedResponse<TransactionResponse>>()).Items).Id);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Contains(_api.Logs.GetSnapshot(), r =>
            r.Message.StartsWith("Read replica unavailable") && r.Message.Contains("transactions list"));
    }

    [Fact]
    public async Task Health_is_degraded_not_unhealthy()
    {
        var response = await _api.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Degraded", await response.Content.ReadAsStringAsync());
    }
}

/// <summary>A working replica (the test database itself, so lag is zero): read-your-writes routing.</summary>
[Collection(ApiCollection.Name)]
public class ReadYourWritesTests : IAsyncLifetime
{
    private readonly ApiFactory _api;

    public ReadYourWritesTests(ContainersFixture db) => _api = new ApiFactory(db, readOnlyConnectionString: db.ConnectionString);

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _api.DisposeAsync().AsTask();

    [Fact]
    public async Task Reads_use_the_replica_until_the_customer_writes_then_the_primary_for_a_few_seconds()
    {
        var customer = await _api.CreateCustomerAsync();
        var tx = await _api.CreateTransactionAsync(customer.Id);
        var client = await _api.CustomerClientAsync(customer);
        bool PinnedToPrimary() => _api.Logs.GetSnapshot().Any(r =>
            r.Message.Contains("pinned to the primary") && r.Message.Contains(customer.Id.ToString()));

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/disputes")).StatusCode);
        Assert.False(PinnedToPrimary());

        Assert.Equal(HttpStatusCode.Created, (await EventsTests.RaiseAsync(client, tx.Id)).StatusCode);
        var ttl = await _api.Redis.KeyTimeToLiveAsync($"rw:{customer.Id:N}");
        Assert.NotNull(ttl);
        Assert.InRange(ttl.Value, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));

        var disputes = await (await client.GetAsync("/api/disputes")).ReadAsync<PagedResponse<DisputeSummaryResponse>>();
        Assert.Single(disputes.Items);
        Assert.True(PinnedToPrimary());
    }
}

/// <summary>Redis can't say whether a customer just wrote, so reads must take the safe route.</summary>
public class ReadYourWritesWithoutRedisTests
{
    [Fact]
    public async Task Marking_does_not_fail_the_write_and_reads_go_to_the_primary()
    {
        // Nothing listens on port 1; short timeouts so the test does not wait out the defaults.
        await using var redis = await ConnectionMultiplexer.ConnectAsync("127.0.0.1:1,abortConnect=false,connectTimeout=500,syncTimeout=500,asyncTimeout=500");
        var readYourWrites = new ReadYourWrites(redis, NullLogger<ReadYourWrites>.Instance);
        var customerId = Guid.NewGuid();

        await readYourWrites.MarkAsync(customerId);

        Assert.True(await readYourWrites.IsRecentAsync(customerId));
    }
}
