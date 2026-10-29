using System.Net.Http.Json;
using DisputePortal.Api.Data;
using DisputePortal.Api.Models;
using DisputePortal.Api.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using StackExchange.Redis;

namespace DisputePortal.IntegrationTests.Infrastructure;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string Password = "Test-password-123";
    public const string SessionCookie = "dp_session";

    private readonly string _connectionString;
    private readonly string _redisConnectionString;
    private readonly int _authPermitsPerMinute;
    private readonly TimeProvider? _clock;

    /// <param name="redisConnectionString">Override to point the API at a Redis that is not there.</param>
    public ApiFactory(ContainersFixture containers, int authPermitsPerMinute = 10_000, TimeProvider? clock = null,
        string? redisConnectionString = null)
    {
        _connectionString = containers.ConnectionString;
        _redisConnectionString = redisConnectionString ?? containers.RedisConnectionString;
        _authPermitsPerMinute = authPermitsPerMinute;
        _clock = clock;
        Redis = containers.Redis;

        // https, so the client's cookie container sends the Secure session cookie back.
        ClientOptions.BaseAddress = new Uri("https://localhost");
    }

    /// <summary>The test Redis, for asserting on session keys and TTLs.</summary>
    public IDatabase Redis { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // "Testing", not Development: no demo seed, no Swagger — the production pipeline.
        builder.UseEnvironment("Testing");

        // UseSetting rather than ConfigureAppConfiguration: Program.cs reads the Redis connection
        // and rate limits before Build(), and only host settings are visible that early.
        builder.UseSetting("ConnectionStrings:DefaultConnection", _connectionString);
        builder.UseSetting("ConnectionStrings:Redis", _redisConnectionString);
        builder.UseSetting("RateLimiting:AuthPermitsPerMinute", _authPermitsPerMinute.ToString());
        builder.UseSetting("RateLimiting:GeneralPermitsPerMinute", "100000");
        builder.UseSetting("Cors:AllowedOrigins:0", "https://portal.example.test");

        if (_clock is not null)
            builder.ConfigureTestServices(services => services.AddSingleton(_clock));

        // In-memory copy of every log record, so tests can assert what was (and was not) logged.
        builder.ConfigureLogging(logging => logging.AddFakeLogging());
    }

    public FakeLogCollector Logs => Services.GetFakeLogCollector();

    public async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public Task WithDbAsync(Func<AppDbContext, Task> action) =>
        WithDbAsync(async db => { await action(db); return true; });

    /// <summary>A new customer with a unique email, so test classes never see each other's data.</summary>
    public Task<Customer> CreateCustomerAsync(string firstName = "Test") => WithDbAsync(async db =>
    {
        var customer = new Customer
        {
            FirstName    = firstName,
            LastName     = "Customer",
            Email        = $"{Guid.NewGuid():N}@example.test",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password, workFactor: 4),
        };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        return customer;
    });

    public Task<Transaction> CreateTransactionAsync(
        Guid customerId,
        decimal amount = 250m,
        TransactionType type = TransactionType.Debit,
        TransactionStatus status = TransactionStatus.Settled,
        int daysAgo = 5,
        string merchant = "Test Merchant") => WithDbAsync(async db =>
    {
        var tx = new Transaction
        {
            CustomerId       = customerId,
            Reference        = $"TXN-T-{Guid.NewGuid():N}"[..30],
            MerchantName     = merchant,
            MerchantCategory = "Testing",
            Amount           = amount,
            Type             = type,
            Channel          = TransactionChannel.Card,
            Status           = status,
            CardLast4        = "1234",
            TransactionDate  = DateTime.UtcNow.AddDays(-daysAgo),
        };
        db.Transactions.Add(tx);
        await db.SaveChangesAsync();
        return tx;
    });

    public static Task<HttpResponseMessage> LoginAsync(HttpClient client, Customer customer) =>
        client.PostAsJsonAsync("/api/auth/login", new { email = customer.Email, password = Password });

    /// <summary>A client signed in as this customer; its cookie container carries the session.</summary>
    public async Task<HttpClient> CustomerClientAsync(Customer customer)
    {
        var client = CreateClient();
        (await LoginAsync(client, customer)).EnsureSuccessStatusCode();
        return client;
    }

    /// <summary>The raw Set-Cookie header for the session cookie on a response.</summary>
    public static string SessionSetCookie(HttpResponseMessage response) =>
        response.Headers.GetValues("Set-Cookie").Single(h => h.StartsWith(SessionCookie + "=", StringComparison.Ordinal));

    /// <summary>The session id a login response set.</summary>
    public static string SessionIdFrom(HttpResponseMessage response)
    {
        var header = SessionSetCookie(response);
        return header[(SessionCookie.Length + 1)..header.IndexOf(';')];
    }

    public static string RedisKeyFor(string sessionId) => RedisSessionStore.KeyFor(sessionId);

    /// <summary>A client with no cookie container of its own, sending exactly this session id.</summary>
    public HttpClient ClientWithSession(string sessionId)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false, BaseAddress = ClientOptions.BaseAddress });
        client.DefaultRequestHeaders.Add("Cookie", $"{SessionCookie}={sessionId}");
        return client;
    }
}
