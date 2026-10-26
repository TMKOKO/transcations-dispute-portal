using System.Net;
using System.Net.Http.Json;
using DisputePortal.Api.Models;
using DisputePortal.IntegrationTests.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace DisputePortal.IntegrationTests;

[Collection(ApiCollection.Name)]
public class LoggingTests : IAsyncLifetime
{
    private const string Description = "Private details the customer typed about this charge.";

    private readonly ApiFactory _api;

    public LoggingTests(PostgresFixture db) => _api = new ApiFactory(db);

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _api.DisposeAsync().AsTask();

    private IReadOnlyList<FakeLogRecord> Logs => _api.Logs.GetSnapshot();

    private static Task<HttpResponseMessage> RaiseAsync(HttpClient client, Guid transactionId, decimal amount = 100m)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/disputes")
        {
            Content = JsonContent.Create(new
            {
                transactionId,
                reason = "DuplicateCharge",
                description = Description,
                disputedAmount = amount,
            })
        };
        request.Headers.Add("Idempotency-Key", $"key-{Guid.NewGuid():N}");
        return client.SendAsync(request);
    }

    [Fact]
    public async Task Failed_login_logs_the_real_reason_but_never_the_credentials()
    {
        var customer = await _api.CreateCustomerAsync();
        const string wrongPassword = "not-the-password-123";

        var response = await _api.CreateClient().PostAsJsonAsync("/api/auth/login",
            new { email = customer.Email, password = wrongPassword });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(Logs, r => r.Level == LogLevel.Warning
                                && r.Message.Contains("wrong password")
                                && r.Message.Contains(customer.Id.ToString()));
        Assert.Contains(Logs, r => r.Level == LogLevel.Warning && r.Message.Contains("invalid_credentials"));
        Assert.DoesNotContain(Logs, r => r.Message.Contains(customer.Email, StringComparison.OrdinalIgnoreCase)
                                      || r.Message.Contains(wrongPassword));
    }

    [Fact]
    public async Task Successful_login_logs_the_lookup_and_the_sign_in()
    {
        var customer = await _api.CreateCustomerAsync();

        await _api.CustomerClientAsync(customer);

        Assert.Contains(Logs, r => r.Level == LogLevel.Information && r.Message.StartsWith("Login lookup: customer " + customer.Id));
        Assert.Contains(Logs, r => r.Level == LogLevel.Information && r.Message.StartsWith($"Customer {customer.Id} signed in"));
    }

    [Fact]
    public async Task Business_rule_failure_logs_its_code_and_reason()
    {
        var customer = await _api.CreateCustomerAsync();
        var tx = await _api.CreateTransactionAsync(customer.Id, status: TransactionStatus.Pending);
        var client = await _api.CustomerClientAsync(customer);

        var response = await RaiseAsync(client, tx.Id);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var failure = Assert.Single(Logs, r => r.Level == LogLevel.Warning && r.Message.Contains("transaction_pending"));
        Assert.Contains("422", failure.Message);
        Assert.Contains(customer.Id.ToString(), failure.Message);
    }

    [Fact]
    public async Task Raising_a_dispute_logs_each_db_call_and_the_result_without_the_description()
    {
        var customer = await _api.CreateCustomerAsync();
        var tx = await _api.CreateTransactionAsync(customer.Id);
        var client = await _api.CustomerClientAsync(customer);

        var response = await RaiseAsync(client, tx.Id);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var info = Logs.Where(r => r.Level == LogLevel.Information).Select(r => r.Message).ToList();
        Assert.Contains(info, m => m.StartsWith("Raise: idempotency key is new"));
        Assert.Contains(info, m => m.StartsWith($"Raise: loaded transaction {tx.Id}"));
        Assert.Contains(info, m => m.StartsWith($"Raise: latest dispute on transaction {tx.Id} is none"));
        Assert.Contains(info, m => m.StartsWith("Raise: allocated dispute sequence"));
        Assert.Contains(info, m => m.Contains($"raised on transaction {tx.Id}"));
        Assert.DoesNotContain(Logs, r => r.Message.Contains(Description));
    }

    [Fact]
    public async Task Validation_failure_logs_field_names_not_values()
    {
        var customer = await _api.CreateCustomerAsync();
        var tx = await _api.CreateTransactionAsync(customer.Id);
        var client = await _api.CustomerClientAsync(customer);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/disputes")
        {
            Content = JsonContent.Create(new { transactionId = tx.Id, reason = "DuplicateCharge", description = "too short", disputedAmount = 10m })
        };
        request.Headers.Add("Idempotency-Key", $"key-{Guid.NewGuid():N}");
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var failure = Assert.Single(Logs, r => r.Level == LogLevel.Warning && r.Message.Contains("failed validation"));
        Assert.Contains("description", failure.Message);
        Assert.DoesNotContain("too short", failure.Message);
    }
}
