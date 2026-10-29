using System.Security.Claims;

namespace DisputePortal.Api.Auth;

/// <summary>The claims a signed-in customer carries, built from their session on every request.</summary>
public static class SessionClaims
{
    public const string CustomerRole = "customer";

    public const string CustomerIdClaim = "customerId";

    private const string RoleClaim = "role";
    private const string SubjectClaim = "sub";

    public static ClaimsPrincipal ForCustomer(Guid customerId, string scheme) => new(new ClaimsIdentity(
    [
        new Claim(RoleClaim, CustomerRole),
        new Claim(CustomerIdClaim, customerId.ToString()),
        new Claim(SubjectClaim, $"customer:{customerId}"),
    ], scheme, SubjectClaim, RoleClaim));
}
