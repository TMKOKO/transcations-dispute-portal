using DisputePortal.Api.Auth;
using DisputePortal.Api.Data;
using DisputePortal.Api.DTOs;
using DisputePortal.Api.Helpers;
using DisputePortal.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DisputePortal.Api.Controllers;

[ApiController]
[Route("api/auth")]
[AllowAnonymous]
public class AuthController : ControllerBase
{
    // Verified against when the email is unknown, so "no such user" costs the same BCrypt work as
    // "wrong password". Without it, response time alone reveals which emails have accounts.
    private static readonly Lazy<string> DummyHash = new(() => BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString()));

    private const string InvalidCredentials = "Email or password is incorrect.";

    private readonly AppDbContext _db;
    private readonly ISessionStore _sessions;
    private readonly CustomerSessionOptions _session;
    private readonly ILogger<AuthController> _logger;

    public AuthController(AppDbContext db, ISessionStore sessions, IOptions<CustomerSessionOptions> session, ILogger<AuthController> logger)
    {
        _db = db;
        _sessions = sessions;
        _session = session.Value;
        _logger = logger;
    }

    /// <summary>Customer login. Starts a server-side session and sets its id as an httpOnly cookie.</summary>
    [HttpPost("login")]
    [ProducesResponseType(typeof(LoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        var email = Normalise(request.Email);
        var customer = await _db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Email == email, ct);
        // Never the email itself: logs are not the place to build a list of who banks here.
        if (customer is null)
            _logger.LogInformation("Login lookup: no matching customer");
        else
            _logger.LogInformation("Login lookup: customer {CustomerId} found", customer.Id);

        if (!Verify(request.Password, customer?.PasswordHash))
        {
            // The real reason stays in the log; the caller gets the same 401 either way.
            if (customer is null)
                _logger.LogWarning("Customer login failed: no account for that email");
            else
                _logger.LogWarning("Customer login failed: wrong password for customer {CustomerId}", customer.Id);

            return this.ProblemWithCode(InvalidCredentials, StatusCodes.Status401Unauthorized, "invalid_credentials");
        }

        // Always a fresh id, and any session the browser already held ends: a planted cookie must
        // never become a signed-in session (session fixation).
        var previous = SessionCookie.Read(Request, _session);
        if (previous is not null)
            await _sessions.DeleteAsync(previous);

        var issued = await _sessions.CreateAsync(customer!.Id);
        SessionCookie.Append(Response, _session, issued.Id);
        _logger.LogInformation("Customer {CustomerId} signed in; session idle timeout {IdleTimeout}", customer.Id, _session.IdleTimeout);
        return Ok(new LoginResponse(customer.FirstName, SessionClaims.CustomerRole, (int)_session.IdleTimeout.TotalSeconds));
    }

    /// <summary>Ends the caller's session, if any, and clears the cookie. Always 204, so a retry is harmless.</summary>
    [HttpPost("logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout()
    {
        var sessionId = SessionCookie.Read(Request, _session);
        if (sessionId is not null)
            await _sessions.DeleteAsync(sessionId);
        SessionCookie.Expire(Response, _session);

        _logger.LogInformation("Customer {CustomerId} signed out", User.GetCustomerId());
        return NoContent();
    }

    private static string Normalise(string email) => email.Trim().ToLowerInvariant();

    private static bool Verify(string password, string? hash)
    {
        var matched = BCrypt.Net.BCrypt.Verify(password, hash ?? DummyHash.Value);
        return hash is not null && matched;
    }
}
