using System.Security.Claims;
using DisputePortal.Api.Services;

namespace DisputePortal.Api.Helpers;

public static class ClaimsPrincipalExtensions
{
    /// <summary>The authenticated customer's id, or null if the token doesn't carry one.</summary>
    public static Guid? GetCustomerId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(JwtTokenService.CustomerIdClaim), out var id) ? id : null;
}
