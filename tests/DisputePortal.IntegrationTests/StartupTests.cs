using System.Net;
using System.Net.Http.Json;
using DisputePortal.Api.Data;
using DisputePortal.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace DisputePortal.IntegrationTests;

/// <summary>What only happens at startup: the Development seed and Swagger, and the migrations matching the model.</summary>
[Collection(ApiCollection.Name)]
public class StartupTests : IAsyncLifetime
{
    private const string DemoPassword = "Demo@12345!";

    private readonly ContainersFixture _containers;
    private readonly List<ApiFactory> _factories = [];

    public StartupTests(ContainersFixture containers) => _containers = containers;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var factory in _factories) await factory.DisposeAsync();
    }

    /// <summary>
    /// A Development start in a database of its own. appsettings.Development.json points at the local
    /// compose stack, so the environment variables are a second lock on top of the factory's settings
    /// (they outrank that file): nothing here may ever migrate or seed the demo database.
    /// </summary>
    private async Task<ApiFactory> StartInDevelopmentAsync(bool agentEnabled)
    {
        var name = $"dev_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(_containers.ConnectionString))
        {
            await connection.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {name}", connection);
            await create.ExecuteNonQueryAsync();
        }
        var connectionString = new NpgsqlConnectionStringBuilder(_containers.ConnectionString) { Database = name }.ConnectionString;

        var api = new ApiFactory(_containers, environment: "Development", connectionString: connectionString, agentEnabled: agentEnabled);
        _factories.Add(api);

        Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", connectionString);
        Environment.SetEnvironmentVariable("ConnectionStrings__Redis", _containers.RedisConnectionString);
        try
        {
            _ = api.Services;   // starts the app: migrations, then the seed
        }
        finally
        {
            Environment.SetEnvironmentVariable("ConnectionStrings__DefaultConnection", null);
            Environment.SetEnvironmentVariable("ConnectionStrings__Redis", null);
        }

        var used = await api.WithDbAsync(db => Task.FromResult(db.Database.GetDbConnection().Database));
        Assert.Equal(name, used);
        return api;
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string email) =>
        client.PostAsJsonAsync("/api/auth/login", new { email, password = DemoPassword });

    [Fact]
    public async Task Development_seeds_demo_customers_who_can_sign_in_and_see_transactions()
    {
        var api = await StartInDevelopmentAsync(agentEnabled: false);
        var client = api.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, "thandi@example.com")).StatusCode);

        var page = await (await client.GetAsync("/api/transactions")).ReadJsonAsync();
        Assert.True(page.GetProperty("totalCount").GetInt32() > 0);
    }

    [Fact]
    public async Task Seeding_again_adds_nothing()
    {
        var api = await StartInDevelopmentAsync(agentEnabled: true);

        var counts = await api.WithDbAsync(async db => (
            Customers: await db.Customers.CountAsync(),
            Transactions: await db.Transactions.CountAsync(),
            Agents: await db.Agents.CountAsync()));

        await api.WithDbAsync(async db =>
        {
            await DbSeeder.SeedAsync(db, TimeProvider.System, NullLogger.Instance);
            await DbSeeder.SeedAgentAsync(db, TimeProvider.System, NullLogger.Instance);
        });

        var after = await api.WithDbAsync(async db => (
            Customers: await db.Customers.CountAsync(),
            Transactions: await db.Transactions.CountAsync(),
            Agents: await db.Agents.CountAsync()));
        Assert.Equal(counts, after);
        Assert.True(counts.Customers > 0 && counts.Transactions > 0 && counts.Agents == 1);
    }

    [Fact]
    public async Task The_demo_agent_is_seeded_only_when_the_agent_login_is_on()
    {
        var off = await StartInDevelopmentAsync(agentEnabled: false);
        Assert.False(await off.WithDbAsync(db => db.Agents.AnyAsync()));
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(off.CreateClient(), "agent@example.com")).StatusCode);

        var on = await StartInDevelopmentAsync(agentEnabled: true);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(on.CreateClient(), "agent@example.com")).StatusCode);
    }

    [Fact]
    public async Task Swagger_is_served_in_Development()
    {
        var api = await StartInDevelopmentAsync(agentEnabled: false);

        var response = await api.CreateClient().GetAsync("/swagger/v1/swagger.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("/api/disputes", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Outside_Development_there_is_no_swagger_and_no_seed()
    {
        var api = new ApiFactory(_containers);
        _factories.Add(api);

        Assert.Equal(HttpStatusCode.NotFound, (await api.CreateClient().GetAsync("/swagger/v1/swagger.json")).StatusCode);
        Assert.False(await api.WithDbAsync(db => db.Customers.AnyAsync(c => c.Email == "thandi@example.com")));
    }

    [Fact]
    public async Task The_migrations_match_the_model()
    {
        var api = new ApiFactory(_containers);
        _factories.Add(api);

        Assert.False(await api.WithDbAsync(db => Task.FromResult(db.Database.HasPendingModelChanges())),
            "The model changed without a migration: run dotnet ef migrations add.");
    }
}
