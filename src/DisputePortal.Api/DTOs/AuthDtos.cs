using System.ComponentModel.DataAnnotations;
using DisputePortal.Api.Helpers;

namespace DisputePortal.Api.DTOs;

public class LoginRequest
{
    [Required]
    [EmailAddress]
    [PlainText]
    // 254: the longest address SMTP allows (RFC 5321).
    [StringLength(254)]
    public string Email { get; set; } = "";

    // Upper bound only: BCrypt ignores input past 72 bytes, and an unbounded field lets a caller
    // make us hash megabytes per request on an anonymous route.
    [Required]
    [StringLength(200)]
    public string Password { get; set; } = "";
}

/// <summary>No token: the session id travels only in the httpOnly cookie.</summary>
public record LoginResponse(string DisplayName, string Role, int IdleTimeoutSeconds);

public record MeResponse(Guid Id, string FirstName, string LastName, string Email);
