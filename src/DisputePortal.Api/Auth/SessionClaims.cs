using System.Security.Claims;

namespace DisputePortal.Api.Auth;

/// <summary>The claims a signed-in customer or agent carries, built from their session on every request.</summary>
public static class SessionClaims
{
    public const string CustomerRole = "customer";
    public const string AgentRole = "agent";

    public const string CustomerIdClaim = "customerId";
    public const string AgentIdClaim = "agentId";

    private const string RoleClaim = "role";
    private const string SubjectClaim = "sub";

    public static ClaimsPrincipal ForCustomer(Guid customerId, string scheme) => new(new ClaimsIdentity(
    [
        new Claim(RoleClaim, CustomerRole),
        new Claim(CustomerIdClaim, customerId.ToString()),
        new Claim(SubjectClaim, $"customer:{customerId}"),
    ], scheme, SubjectClaim, RoleClaim));

    // No customerId claim: nothing an agent does can resolve to a customer's own data.
    public static ClaimsPrincipal ForAgent(Guid agentId, string scheme) => new(new ClaimsIdentity(
    [
        new Claim(RoleClaim, AgentRole),
        new Claim(AgentIdClaim, agentId.ToString()),
        new Claim(SubjectClaim, $"agent:{agentId}"),
    ], scheme, SubjectClaim, RoleClaim));
}
