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

    public SessionAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ISessionStore sessions,
        IOptions<CustomerSessionOptions> session)
        : base(options, logger, encoder)
    {
        _sessions = sessions;
        _session  = session.Value;
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
        Logger.LogDebug("Session lookup: live session for customer {CustomerId}", session.CustomerId);
        var principal = SessionClaims.ForCustomer(session.CustomerId, Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        // A dead cookie would otherwise cost a Redis lookup on every request until the tab closes.
        if (SessionCookie.Read(Request, _session) is not null)
            SessionCookie.Expire(Response, _session);

        return ProblemResponses.WriteAsync(Context, Logger, StatusCodes.Status401Unauthorized,
            "unauthenticated", "Your session has ended. Please sign in again.");
    }
}
