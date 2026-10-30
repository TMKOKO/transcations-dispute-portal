using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DisputePortal.Api.Auth;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace DisputePortal.Api.Services;

/// <summary>Who a session belongs to: a customer or an agent, never both.</summary>
public sealed record SessionData(Guid? CustomerId, Guid? AgentId, DateTimeOffset CreatedAt);

/// <summary>A new session and the id that goes in the cookie. The id is never stored or logged.</summary>
public sealed record IssuedSession(string Id, SessionData Session);

public interface ISessionStore
{
    Task<IssuedSession> CreateAsync(Guid customerId);

    Task<IssuedSession> CreateForAgentAsync(Guid agentId);

    /// <summary>The live session for this id, with its idle timer restarted; null if it has ended.</summary>
    Task<SessionData?> TouchAsync(string sessionId);

    Task DeleteAsync(string sessionId);
}

public sealed class RedisSessionStore : ISessionStore
{
    // 32 random bytes, base64url without padding.
    private const int IdLength = 43;

    private readonly IConnectionMultiplexer _redis;
    private readonly CustomerSessionOptions _options;
    private readonly TimeProvider _clock;

    public RedisSessionStore(IConnectionMultiplexer redis, IOptions<CustomerSessionOptions> options, TimeProvider clock)
    {
        _redis   = redis;
        _options = options.Value;
        _clock   = clock;
    }

    private IDatabase Db => _redis.GetDatabase();

    /// <summary>
    /// Keyed by a hash of the id, so a Redis dump or MONITOR output holds nothing a caller could
    /// put in a cookie.
    /// </summary>
    internal static string KeyFor(string sessionId) =>
        "session:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId))).ToLowerInvariant();

    public Task<IssuedSession> CreateAsync(Guid customerId) =>
        StoreAsync(new SessionData(customerId, null, _clock.GetUtcNow()));

    public Task<IssuedSession> CreateForAgentAsync(Guid agentId) =>
        StoreAsync(new SessionData(null, agentId, _clock.GetUtcNow()));

    private async Task<IssuedSession> StoreAsync(SessionData session)
    {
        var id = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

        await Db.StringSetAsync(KeyFor(id), JsonSerializer.Serialize(session), _options.IdleTimeout);
        return new IssuedSession(id, session);
    }

    public async Task<SessionData?> TouchAsync(string sessionId)
    {
        // Anything else cannot be one of ours; skip the round trip.
        if (sessionId.Length != IdLength) return null;

        var key = KeyFor(sessionId);
        // GETEX: read and restart the idle timer in one atomic command.
        var value = await Db.StringGetSetExpiryAsync(key, _options.IdleTimeout);
        if (value.IsNullOrEmpty) return null;

        var session = JsonSerializer.Deserialize<SessionData>(value.ToString());
        if (session is null)
        {
            await Db.KeyDeleteAsync(key);
            return null;
        }

        var remaining = session.CreatedAt + _options.AbsoluteLifetime - _clock.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
        {
            await Db.KeyDeleteAsync(key);
            return null;
        }

        // Near the cap, the idle timer must not carry the session past it.
        if (remaining < _options.IdleTimeout)
            await Db.KeyExpireAsync(key, remaining);

        return session;
    }

    public Task DeleteAsync(string sessionId) =>
        sessionId.Length == IdLength ? Db.KeyDeleteAsync(KeyFor(sessionId)) : Task.CompletedTask;
}
