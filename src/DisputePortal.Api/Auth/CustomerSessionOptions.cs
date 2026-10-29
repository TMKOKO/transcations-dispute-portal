namespace DisputePortal.Api.Auth;

/// <summary>Bound from the <c>Session</c> configuration section.</summary>
public class CustomerSessionOptions
{
    public const string SectionName = "Session";

    /// <summary>A session with no request for this long ends. Every authenticated request restarts it.</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>A session ends this long after sign-in however busy it is.</summary>
    public TimeSpan AbsoluteLifetime { get; set; } = TimeSpan.FromHours(8);

    public string CookieName { get; set; } = "dp_session";

    /// <summary>Off only for local plain-http stacks; browsers and Postman drop Secure cookies over http.</summary>
    public bool SecureCookie { get; set; } = true;

    public static string RequireRedis(IConfiguration config)
    {
        var connection = config.GetConnectionString("Redis");
        if (string.IsNullOrWhiteSpace(connection))
            throw new InvalidOperationException(
                "ConnectionStrings:Redis is missing. Sessions live in Redis; set it via the ConnectionStrings__Redis environment variable.");
        return connection;
    }
}
