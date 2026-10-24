using DisputePortal.Api.Data;
using DisputePortal.Api.DTOs;
using DisputePortal.Api.Helpers;
using DisputePortal.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DisputePortal.Api.Controllers;

[ApiController]
[Route("api/me")]
[Authorize(Roles = JwtTokenService.CustomerRole)]
public class MeController : ControllerBase
{
    private readonly AppDbContext _db;

    public MeController(AppDbContext db) => _db = db;

    /// <summary>The signed-in customer's profile.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(MeResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var customerId = User.GetCustomerId();
        if (customerId is null) return Unauthorized();

        var me = await _db.Customers.AsNoTracking()
            .Where(c => c.Id == customerId)
            .Select(c => new MeResponse(c.Id, c.FirstName, c.LastName, c.Email))
            .FirstOrDefaultAsync(ct);

        // A valid token for a customer that no longer exists: treat as signed out.
        return me is null ? Unauthorized() : Ok(me);
    }
}
