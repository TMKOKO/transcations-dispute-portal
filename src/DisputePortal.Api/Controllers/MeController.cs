using DisputePortal.Api.Auth;
using DisputePortal.Api.DTOs;
using DisputePortal.Api.Helpers;
using DisputePortal.Api.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DisputePortal.Api.Controllers;

[ApiController]
[Route("api/me")]
[Authorize(Roles = SessionClaims.CustomerRole)]
public class MeController : ControllerBase
{
    private readonly MeViewModel _me;

    public MeController(MeViewModel me)
    {
        _me = me;
    }

    /// <summary>The signed-in customer's profile.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(MeResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var customerId = User.GetCustomerId();
        if (customerId is null) return Unauthorized();

        // A live session for a customer that no longer exists: treat as signed out.
        var me = await _me.GetAsync(customerId.Value, ct);
        return me is null ? Unauthorized() : Ok(me);
    }
}
