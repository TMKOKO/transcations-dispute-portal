using DisputePortal.Api.Auth;
using DisputePortal.Api.Data;
using DisputePortal.Api.DTOs;
using DisputePortal.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DisputePortal.Api.ViewModels;

/// <summary>A new session: its id goes only into the cookie, the response into the body.</summary>
public sealed record SignedIn(string SessionId, LoginResponse Response);

/// <summary>Sign-in and sign-out for <c>AuthController</c>. The controller owns the cookie.</summary>
public sealed class AuthViewModel
{
    // Verified against when the email is unknown, so "no such user" costs the same BCrypt work as
    // "wrong password". Without it, response time alone reveals which emails have accounts.
    private static readonly Lazy<string> DummyHash = new(() => BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString()));

    private const string InvalidCredentials = "Email or password is incorrect.";
    public const string InvalidCredentialsCode = "invalid_credentials";

    private readonly AppDbContext _db;
    private readonly ISessionStore _sessions;
    private readonly CustomerSessionOptions _session;
    private readonly AgentOptions _agents;
    private readonly ILogger<AuthViewModel> _logger;

    public AuthViewModel(AppDbContext db, ISessionStore sessions, IOptions<CustomerSessionOptions> session,
        IOptions<AgentOptions> agents, ILogger<AuthViewModel> logger)
    {
        _db = db;
        _sessions = sessions;
        _session = session.Value;
        _agents = agents.Value;
        _logger = logger;
    }

    /// <summary>
    /// Checks the credentials and starts a session. Fails with <see cref="InvalidCredentialsCode"/>
    /// whatever the reason, which the controller returns as a 401.
    /// </summary>
    public async Task<ServiceResult<SignedIn>> LoginAsync(LoginRequest request, string? previousSessionId, CancellationToken ct)
    {
        var email = Normalise(request.Email);
        var customer = await _db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Email == email, ct);
        // Never the email itself: logs are not the place to build a list of who banks here.
        if (customer is null)
            _logger.LogInformation("Login lookup: no matching customer");
        else
            _logger.LogInformation("Login lookup: customer {CustomerId} found", customer.Id);

        var agent = customer is null && _agents.Enabled
            ? await _db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Email == email, ct)
            : null;
        if (agent is not null)
            _logger.LogInformation("Login lookup: agent {AgentId} found", agent.Id);

        if (!Verify(request.Password, customer?.PasswordHash ?? agent?.PasswordHash))
        {
            // The real reason stays in the log; the caller gets the same 401 either way.
            if (agent is not null)
                _logger.LogWarning("Agent login failed: wrong password for agent {AgentId}", agent.Id);
            else if (customer is null)
                _logger.LogWarning("Customer login failed: no account for that email");
            else
                _logger.LogWarning("Customer login failed: wrong password for customer {CustomerId}", customer.Id);

            return ServiceResult<SignedIn>.Fail(ServiceErrorKind.BusinessRule, InvalidCredentialsCode, InvalidCredentials);
        }

        // Always a fresh id, and any session the browser already held ends: a planted cookie must
        // never become a signed-in session (session fixation).
        if (previousSessionId is not null)
            await _sessions.DeleteAsync(previousSessionId);

        if (agent is not null)
        {
            var agentSession = await _sessions.CreateForAgentAsync(agent.Id);
            _logger.LogInformation("Agent {AgentId} signed in; session idle timeout {IdleTimeout}", agent.Id, _session.IdleTimeout);
            return ServiceResult<SignedIn>.Ok(new SignedIn(agentSession.Id,
                new LoginResponse(agent.DisplayName, SessionClaims.AgentRole, (int)_session.IdleTimeout.TotalSeconds)));
        }

        var issued = await _sessions.CreateAsync(customer!.Id);
        _logger.LogInformation("Customer {CustomerId} signed in; session idle timeout {IdleTimeout}", customer.Id, _session.IdleTimeout);
        return ServiceResult<SignedIn>.Ok(new SignedIn(issued.Id,
            new LoginResponse(customer.FirstName, SessionClaims.CustomerRole, (int)_session.IdleTimeout.TotalSeconds)));
    }

    /// <summary>Ends the session, if there is one.</summary>
    public async Task LogoutAsync(string? sessionId, Guid? customerId, Guid? agentId)
    {
        if (sessionId is not null)
            await _sessions.DeleteAsync(sessionId);

        if (agentId is not null)
            _logger.LogInformation("Agent {AgentId} signed out", agentId);
        else
            _logger.LogInformation("Customer {CustomerId} signed out", customerId);
    }

    private static string Normalise(string email) => email.Trim().ToLowerInvariant();

    private static bool Verify(string password, string? hash)
    {
        var matched = BCrypt.Net.BCrypt.Verify(password, hash ?? DummyHash.Value);
        return hash is not null && matched;
    }
}
