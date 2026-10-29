using System.Security.Claims;
using DisputePortal.Api.Auth;

namespace DisputePortal.Api.Helpers;

public static class ClaimsPrincipalExtensions
{
    /// <summary>The authenticated customer's id, or null if the request has no session.</summary>
    public static Guid? GetCustomerId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(SessionClaims.CustomerIdClaim), out var id) ? id : null;
}
