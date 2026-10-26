using System.Net.Http.Headers;
using System.Net.Http.Json;
using DisputePortal.Api.Data;
using DisputePortal.Api.DTOs;
using DisputePortal.Api.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace DisputePortal.IntegrationTests.Infrastructure;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string Password = "Test-password-123";

    private readonly string _connectionString;
    private readonly int _authPermitsPerMinute;

    public ApiFactory(PostgresFixture db, int authPermitsPerMinute = 10_000)
    {
        _connectionString = db.ConnectionString;
        _authPermitsPerMinute = authPermitsPerMinute;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // "Testing", not Development: no demo seed, no Swagger — the production pipeline.
        builder.UseEnvironment("Testing");

        // UseSetting rather than ConfigureAppConfiguration: Program.cs reads the JWT key and rate
        // limits before Build(), and only host settings are visible that early.
        builder.UseSetting("ConnectionStrings:DefaultConnection", _connectionString);
        builder.UseSetting("Jwt:Key", "integration-tests-signing-key-at-least-32-bytes!");
        builder.UseSetting("RateLimiting:AuthPermitsPerMinute", _authPermitsPerMinute.ToString());
        builder.UseSetting("RateLimiting:GeneralPermitsPerMinute", "100000");

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

    public async Task<HttpClient> CustomerClientAsync(Customer customer)
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { email = customer.Email, password = Password });
        response.EnsureSuccessStatusCode();
        var login = await response.Content.ReadFromJsonAsync<LoginResponse>(Json.Options);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.Token);
        return client;
    }
}
