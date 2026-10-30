using System.Text.Encodings.Web;
using DisputePortal.Api.Helpers;
using DisputePortal.Api.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace DisputePortal.Api.Auth;

/// <summary>
/// Authenticates a request from the session cookie: one Redis GETEX per request, which also
/// restarts the idle timer. A Redis outage is not caught here; it surfaces as an exception and
/// <see cref="SessionStoreUnavailableHandler"/> turns it into a 503, never a 401.
/// </summary>
public sealed class SessionAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Session";

    private readonly ISessionStore _sessions;
    private readonly CustomerSessionOptions _session;
    private readonly AgentOptions _agents;

    public SessionAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ISessionStore sessions,
        IOptions<CustomerSessionOptions> session,
        IOptions<AgentOptions> agents)
        : base(options, logger, encoder)
    {
        _sessions = sessions;
        _session  = session.Value;
        _agents   = agents.Value;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var sessionId = SessionCookie.Read(Request, _session);
        if (sessionId is null) return AuthenticateResult.NoResult();

        var session = await _sessions.TouchAsync(sessionId);
        if (session is null)
        {
            Logger.LogInformation("Session lookup: no live session for the presented cookie");
            return AuthenticateResult.Fail("Session ended or unknown.");
        }

        // Debug, not Information: this runs on every authenticated request.
        if (session.AgentId is { } agentId)
        {
            // Turning the demo login off also ends the agent sessions still in Redis.
            if (!_agents.Enabled)
            {
                Logger.LogInformation("Session lookup: agent {AgentId} refused, the agent login is off", agentId);
                return AuthenticateResult.Fail("Agent login is off.");
            }

            Logger.LogDebug("Session lookup: live session for agent {AgentId}", agentId);
            return AuthenticateResult.Success(new AuthenticationTicket(SessionClaims.ForAgent(agentId, Scheme.Name), Scheme.Name));
        }

        if (session.CustomerId is not { } customerId)
        {
            Logger.LogWarning("Session lookup: session has neither a customer nor an agent");
            return AuthenticateResult.Fail("Session ended or unknown.");
        }

        Logger.LogDebug("Session lookup: live session for customer {CustomerId}", customerId);
        return AuthenticateResult.Success(new AuthenticationTicket(SessionClaims.ForCustomer(customerId, Scheme.Name), Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        // A dead cookie would otherwise cost a Redis lookup on every request until the tab closes.
        if (SessionCookie.Read(Request, _session) is not null)
            SessionCookie.Expire(Response, _session);

        return ProblemResponses.WriteAsync(Context, Logger, StatusCodes.Status401Unauthorized,
            "unauthenticated", "Your session has ended. Please sign in again.");
    }

    // A live session in the wrong role: an agent on a customer endpoint, or the other way round.
    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        ProblemResponses.WriteAsync(Context, Logger, StatusCodes.Status403Forbidden,
            "forbidden", "You do not have access to this.");
}
