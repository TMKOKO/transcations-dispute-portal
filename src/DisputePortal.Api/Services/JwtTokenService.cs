using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using DisputePortal.Api.Models;
using Microsoft.IdentityModel.Tokens;

namespace DisputePortal.Api.Services;

public sealed record IssuedToken(string Token, DateTime ExpiresAt);

public interface ITokenService
{
    /// <summary>Issue a JWT for a customer (carries the CustomerId).</summary>
    IssuedToken CreateCustomerToken(Customer customer);
}

public sealed class JwtTokenService : ITokenService
{
    public const string CustomerRole = "customer";

    public const string CustomerIdClaim = "customerId";

    private readonly SigningCredentials _credentials;
    private readonly string _issuer;
    private readonly string _audience;
    private readonly TimeSpan _lifetime;
    private readonly TimeProvider _clock;

    public JwtTokenService(IConfiguration config, TimeProvider clock)
    {
        _credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtSettings.RequireKey(config))),
            SecurityAlgorithms.HmacSha256);

        _issuer   = JwtSettings.Issuer(config);
        _audience = JwtSettings.Audience(config);
        // Short-lived: this is a bank-facing session with no refresh token (see docs/adr/0003).
        _lifetime = TimeSpan.FromMinutes(config.GetValue("Jwt:LifetimeMinutes", 30));
        _clock    = clock;
    }

    public IssuedToken CreateCustomerToken(Customer customer) => Create(
    [
        new Claim("role", CustomerRole),
        new Claim(CustomerIdClaim, customer.Id.ToString()),
        new Claim(JwtRegisteredClaimNames.Sub, $"customer:{customer.Id}"),
    ]);

    private IssuedToken Create(IEnumerable<Claim> claims)
    {
        var now     = _clock.GetUtcNow().UtcDateTime;
        var expires = now.Add(_lifetime);

        var token = new JwtSecurityToken(
            issuer: _issuer,
            audience: _audience,
            claims: claims.Append(new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())),
            notBefore: now,
            expires: expires,
            signingCredentials: _credentials);

        return new IssuedToken(new JwtSecurityTokenHandler().WriteToken(token), expires);
    }
}

/// <summary>JWT settings shared by token issuing and validation, so the two cannot disagree.</summary>
public static class JwtSettings
{
    public static string RequireKey(IConfiguration config)
    {
        var key = config["Jwt:Key"];
        if (string.IsNullOrWhiteSpace(key) || Encoding.UTF8.GetByteCount(key) < 32)
            throw new InvalidOperationException(
                "Jwt:Key is missing or too short. Set a secret of at least 32 bytes (256 bits) via the Jwt__Key environment variable.");
        return key;
    }

    public static string Issuer(IConfiguration config)   => config["Jwt:Issuer"]   ?? "dispute-portal";
    public static string Audience(IConfiguration config) => config["Jwt:Audience"] ?? "dispute-portal";
}
