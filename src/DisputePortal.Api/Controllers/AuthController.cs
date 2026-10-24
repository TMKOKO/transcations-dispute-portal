using DisputePortal.Api.Data;
using DisputePortal.Api.DTOs;
using DisputePortal.Api.Helpers;
using DisputePortal.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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
    private readonly ITokenService _tokens;
    private readonly ILogger<AuthController> _logger;

    public AuthController(AppDbContext db, ITokenService tokens, ILogger<AuthController> logger)
    {
        _db = db;
        _tokens = tokens;
        _logger = logger;
    }

    /// <summary>Customer login. Returns a short-lived bearer token.</summary>
    [HttpPost("login")]
    [ProducesResponseType(typeof(LoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        var email = Normalise(request.Email);
        var customer = await _db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Email == email, ct);

        if (!Verify(request.Password, customer?.PasswordHash))
        {
            _logger.LogWarning("Failed customer login");
            return this.ProblemWithCode(InvalidCredentials, StatusCodes.Status401Unauthorized, "invalid_credentials");
        }

        var token = _tokens.CreateCustomerToken(customer!);
        return Ok(new LoginResponse(token.Token, token.ExpiresAt, customer!.FirstName, JwtTokenService.CustomerRole));
    }

    /// <summary>Back-office agent login (used from Postman; there is no agent UI).</summary>
    [HttpPost("agent-login")]
    [ProducesResponseType(typeof(LoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> AgentLogin([FromBody] LoginRequest request, CancellationToken ct)
    {
        var email = Normalise(request.Email);
        var agent = await _db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Email == email, ct);

        if (!Verify(request.Password, agent?.PasswordHash))
        {
            _logger.LogWarning("Failed agent login");
            return this.ProblemWithCode(InvalidCredentials, StatusCodes.Status401Unauthorized, "invalid_credentials");
        }

        var token = _tokens.CreateAgentToken(agent!);
        return Ok(new LoginResponse(token.Token, token.ExpiresAt, agent!.DisplayName, JwtTokenService.AgentRole));
    }

    private static string Normalise(string email) => email.Trim().ToLowerInvariant();

    private static bool Verify(string password, string? hash)
    {
        var matched = BCrypt.Net.BCrypt.Verify(password, hash ?? DummyHash.Value);
        return hash is not null && matched;
    }
}
