using System.Net;
using System.Net.Http.Json;
using DisputePortal.Api.DTOs;
using DisputePortal.IntegrationTests.Infrastructure;

namespace DisputePortal.IntegrationTests;

[Collection(ApiCollection.Name)]
public class AuthTests : IAsyncLifetime
{
    private readonly ApiFactory _api;

    public AuthTests(PostgresFixture db) => _api = new ApiFactory(db);

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _api.DisposeAsync().AsTask();

    [Fact]
    public async Task Login_with_valid_credentials_returns_a_token()
    {
        var customer = await _api.CreateCustomerAsync("Thandi");
        var client = _api.CreateClient();

        // Email matching is case- and whitespace-insensitive.
        var response = await client.PostAsJsonAsync("/api/auth/login",
            new { email = "  " + customer.Email.ToUpperInvariant(), password = ApiFactory.Password });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.ReadAsync<LoginResponse>();
        Assert.False(string.IsNullOrWhiteSpace(body.Token));
        Assert.Equal("Thandi", body.DisplayName);
        Assert.Equal("customer", body.Role);
        Assert.True(body.ExpiresAt > DateTime.UtcNow);
    }

    [Fact]
    public async Task Wrong_password_and_unknown_email_get_the_same_401()
    {
        var customer = await _api.CreateCustomerAsync();
        var client = _api.CreateClient();

        var wrongPassword = await client.PostAsJsonAsync("/api/auth/login",
            new { email = customer.Email, password = "not-the-password" });
        var unknownEmail = await client.PostAsJsonAsync("/api/auth/login",
            new { email = "nobody@example.test", password = ApiFactory.Password });

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownEmail.StatusCode);
        Assert.Equal("invalid_credentials", await wrongPassword.ProblemCodeAsync());
        Assert.Equal("invalid_credentials", await unknownEmail.ProblemCodeAsync());
    }

    [Fact]
    public async Task Customer_credentials_do_not_work_on_agent_login()
    {
        var customer = await _api.CreateCustomerAsync();
        var response = await _api.CreateClient().PostAsJsonAsync("/api/auth/agent-login",
            new { email = customer.Email, password = ApiFactory.Password });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Protected_routes_require_a_token()
    {
        var client = _api.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/transactions")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/disputes")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task Tampered_token_is_rejected()
    {
        var customer = await _api.CreateCustomerAsync();
        var client = await _api.CustomerClientAsync(customer);
        var token = client.DefaultRequestHeaders.Authorization!.Parameter!;
        client.DefaultRequestHeaders.Authorization = new("Bearer", token[..^4] + "AAAA");

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task Me_returns_the_callers_profile()
    {
        var customer = await _api.CreateCustomerAsync("Sipho");
        var client = await _api.CustomerClientAsync(customer);

        var me = await (await client.GetAsync("/api/me")).ReadAsync<MeResponse>();

        Assert.Equal(customer.Id, me.Id);
        Assert.Equal("Sipho", me.FirstName);
        Assert.Equal(customer.Email, me.Email);
    }

    [Fact]
    public async Task Responses_carry_security_headers()
    {
        var response = await _api.CreateClient().GetAsync("/health");

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.False(response.Headers.Contains("Server"));
    }
}

[Collection(ApiCollection.Name)]
public class RateLimitTests : IAsyncLifetime
{
    private readonly ApiFactory _api;

    public RateLimitTests(PostgresFixture db) => _api = new ApiFactory(db, authPermitsPerMinute: 3);

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _api.DisposeAsync().AsTask();

    [Fact]
    public async Task Login_attempts_beyond_the_limit_get_429()
    {
        var client = _api.CreateClient();
        var payload = new { email = "nobody@example.test", password = "guess" };

        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/login", payload)).StatusCode);

        var limited = await client.PostAsJsonAsync("/api/auth/login", payload);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("rate_limited", await limited.ProblemCodeAsync());
        Assert.True(limited.Headers.Contains("Retry-After"));
    }
}
