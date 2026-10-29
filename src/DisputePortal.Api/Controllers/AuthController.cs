using DisputePortal.Api.Auth;
using DisputePortal.Api.DTOs;
using DisputePortal.Api.Helpers;
using DisputePortal.Api.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DisputePortal.Api.Controllers;

[ApiController]
[Route("api/auth")]
[AllowAnonymous]
public class AuthController : ControllerBase
{
    private readonly AuthViewModel _auth;
    private readonly CustomerSessionOptions _session;

    public AuthController(AuthViewModel auth, IOptions<CustomerSessionOptions> session)
    {
        _auth = auth;
        _session = session.Value;
    }

    /// <summary>Customer login. Starts a server-side session and sets its id as an httpOnly cookie.</summary>
    [HttpPost("login")]
    [ProducesResponseType(typeof(LoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        var result = await _auth.LoginAsync(request, SessionCookie.Read(Request, _session), ct);
        if (!result.IsSuccess)
            return this.ProblemWithCode(result.Error!.Message, StatusCodes.Status401Unauthorized, result.Error.Code);

        SessionCookie.Append(Response, _session, result.Value!.SessionId);
        return Ok(result.Value.Response);
    }

    /// <summary>Ends the caller's session, if any, and clears the cookie. Always 204, so a retry is harmless.</summary>
    [HttpPost("logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout()
    {
        await _auth.LogoutAsync(SessionCookie.Read(Request, _session), User.GetCustomerId());
        SessionCookie.Expire(Response, _session);
        return NoContent();
    }
}
