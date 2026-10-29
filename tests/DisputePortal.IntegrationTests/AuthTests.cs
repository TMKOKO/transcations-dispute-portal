using System.Net;
using System.Net.Http.Json;
using DisputePortal.Api.DTOs;
using DisputePortal.IntegrationTests.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace DisputePortal.IntegrationTests;

[Collection(ApiCollection.Name)]
public class AuthTests : IAsyncLifetime
{
    private readonly ApiFactory _api;

    public AuthTests(ContainersFixture db) => _api = new ApiFactory(db);

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _api.DisposeAsync().AsTask();

    [Fact]
    public async Task Login_with_valid_credentials_sets_an_httponly_session_cookie_and_returns_no_token()
    {
        var customer = await _api.CreateCustomerAsync("Thandi");
        var client = _api.CreateClient();

        // Email matching is case- and whitespace-insensitive.
        var response = await client.PostAsJsonAsync("/api/auth/login",
            new { email = "  " + customer.Email.ToUpperInvariant(), password = ApiFactory.Password });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.ReadJsonAsync();
        Assert.Equal("Thandi", body.GetProperty("displayName").GetString());
        Assert.Equal("customer", body.GetProperty("role").GetString());
        Assert.Equal(300, body.GetProperty("idleTimeoutSeconds").GetInt32());
        Assert.False(body.TryGetProperty("token", out _));

        var cookie = ApiFactory.SessionSetCookie(response).ToLowerInvariant();
        Assert.Contains("httponly", cookie);
        Assert.Contains("secure", cookie);
        Assert.Contains("samesite=strict", cookie);
        Assert.Contains("path=/", cookie);
        Assert.DoesNotContain("max-age", cookie);
        Assert.DoesNotContain("expires", cookie);
    }

    [Fact]
    public async Task Login_stores_the_session_under_a_hash_with_the_idle_timeout()
    {
        var customer = await _api.CreateCustomerAsync();
        var response = await ApiFactory.LoginAsync(_api.CreateClient(), customer);
        var sessionId = ApiFactory.SessionIdFrom(response);

        Assert.False(await _api.Redis.KeyExistsAsync("session:" + sessionId));
        var ttl = await _api.Redis.KeyTimeToLiveAsync(ApiFactory.RedisKeyFor(sessionId));
        Assert.InRange(ttl!.Value, TimeSpan.FromSeconds(290), TimeSpan.FromSeconds(300));
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
        Assert.False(wrongPassword.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Protected_routes_require_a_session()
    {
        var client = _api.CreateClient();

        var me = await client.GetAsync("/api/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        Assert.Equal("unauthenticated", await me.ProblemCodeAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/transactions")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/disputes")).StatusCode);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")] // the right length, but never issued
    public async Task Unknown_session_cookie_is_rejected_and_cleared(string sessionId)
    {
        var response = await _api.ClientWithSession(sessionId).GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("unauthenticated", await response.ProblemCodeAsync());
        Assert.Contains("expires=", ApiFactory.SessionSetCookie(response), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Each_request_restarts_the_idle_timer()
    {
        var customer = await _api.CreateCustomerAsync();
        var client = _api.CreateClient();
        var key = ApiFactory.RedisKeyFor(ApiFactory.SessionIdFrom(await ApiFactory.LoginAsync(client, customer)));

        // As if the customer had been idle for almost five minutes.
        await _api.Redis.KeyExpireAsync(key, TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/me")).StatusCode);

        var ttl = await _api.Redis.KeyTimeToLiveAsync(key);
        Assert.InRange(ttl!.Value, TimeSpan.FromSeconds(290), TimeSpan.FromSeconds(300));
    }

    [Fact]
    public async Task Session_ends_after_the_idle_timeout()
    {
        var customer = await _api.CreateCustomerAsync();
        var client = _api.CreateClient();
        var key = ApiFactory.RedisKeyFor(ApiFactory.SessionIdFrom(await ApiFactory.LoginAsync(client, customer)));

        // Let Redis expire it for real rather than deleting it.
        await _api.Redis.KeyExpireAsync(key, TimeSpan.FromMilliseconds(200));
        await Task.Delay(500);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task Logout_revokes_the_session_on_the_server()
    {
        var customer = await _api.CreateCustomerAsync();
        var client = _api.CreateClient();
        var sessionId = ApiFactory.SessionIdFrom(await ApiFactory.LoginAsync(client, customer));

        var logout = await client.PostAsync("/api/auth/logout", null);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Contains("expires=", ApiFactory.SessionSetCookie(logout), StringComparison.OrdinalIgnoreCase);
        Assert.False(await _api.Redis.KeyExistsAsync(ApiFactory.RedisKeyFor(sessionId)));
        // A copy of the cookie kept elsewhere is useless now.
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.ClientWithSession(sessionId).GetAsync("/api/me")).StatusCode);
        // And signing out again is harmless.
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/auth/logout", null)).StatusCode);
    }

    [Fact]
    public async Task Signing_in_again_replaces_the_previous_session()
    {
        var customer = await _api.CreateCustomerAsync();
        var client = _api.CreateClient();
        var first = ApiFactory.SessionIdFrom(await ApiFactory.LoginAsync(client, customer));

        var second = ApiFactory.SessionIdFrom(await ApiFactory.LoginAsync(client, customer));

        Assert.NotEqual(first, second);
        Assert.False(await _api.Redis.KeyExistsAsync(ApiFactory.RedisKeyFor(first)));
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.ClientWithSession(first).GetAsync("/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task State_changing_requests_from_a_foreign_origin_are_refused()
    {
        var customer = await _api.CreateCustomerAsync();
        var client = await _api.CustomerClientAsync(customer);

        var foreign = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        foreign.Headers.Add("Origin", "https://evil.example");
        var refused = await client.SendAsync(foreign);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("origin_not_allowed", await refused.ProblemCodeAsync());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/me")).StatusCode);

        var portal = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        portal.Headers.Add("Origin", "https://portal.example.test");
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(portal)).StatusCode);
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
public class SessionLifetimeTests : IAsyncLifetime
{
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly ApiFactory _api;

    public SessionLifetimeTests(ContainersFixture db) => _api = new ApiFactory(db, clock: _clock);

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _api.DisposeAsync().AsTask();

    [Fact]
    public async Task A_busy_session_still_ends_at_the_absolute_cap()
    {
        var customer = await _api.CreateCustomerAsync();
        var client = _api.CreateClient();
        var key = ApiFactory.RedisKeyFor(ApiFactory.SessionIdFrom(await ApiFactory.LoginAsync(client, customer)));

        // One minute before the cap: still signed in, but the idle timer may not run past the cap.
        _clock.Advance(TimeSpan.FromHours(8) - TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/me")).StatusCode);
        var ttl = await _api.Redis.KeyTimeToLiveAsync(key);
        Assert.InRange(ttl!.Value, TimeSpan.FromSeconds(50), TimeSpan.FromSeconds(60));

        _clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me")).StatusCode);
        Assert.False(await _api.Redis.KeyExistsAsync(key));
    }
}

[Collection(ApiCollection.Name)]
public class SessionStoreDownTests : IAsyncLifetime
{
    private readonly ApiFactory _api;

    // Nothing listens on port 1; short timeouts so the test does not wait out the defaults.
    public SessionStoreDownTests(ContainersFixture db) =>
        _api = new ApiFactory(db, redisConnectionString: "127.0.0.1:1,connectTimeout=500,syncTimeout=500,asyncTimeout=500");

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _api.DisposeAsync().AsTask();

    [Fact]
    public async Task Requests_with_a_session_fail_closed_with_503_not_401()
    {
        var response = await _api.ClientWithSession(new string('A', 43)).GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("session_store_unavailable", await response.ProblemCodeAsync());
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Login_fails_closed_with_503()
    {
        var customer = await _api.CreateCustomerAsync();

        var response = await ApiFactory.LoginAsync(_api.CreateClient(), customer);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("session_store_unavailable", await response.ProblemCodeAsync());
    }

    [Fact]
    public async Task Health_reports_unhealthy()
    {
        var response = await _api.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
}

[Collection(ApiCollection.Name)]
public class RateLimitTests : IAsyncLifetime
{
    private readonly ApiFactory _api;

    public RateLimitTests(ContainersFixture db) => _api = new ApiFactory(db, authPermitsPerMinute: 3);

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
        Assert.Contains(_api.Logs.GetSnapshot(), r => r.Level == LogLevel.Warning
                                                   && r.Message.Contains("429 rate_limited: auth limit reached"));
        // Signing out is not a credential guess and has its own budget.
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/auth/logout", null)).StatusCode);
    }
}
