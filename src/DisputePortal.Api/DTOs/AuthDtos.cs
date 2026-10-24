using System.ComponentModel.DataAnnotations;

namespace DisputePortal.Api.DTOs;

public class LoginRequest
{
    [Required]
    [EmailAddress]
    [StringLength(200)]
    public string Email { get; set; } = "";

    // Upper bound only: BCrypt ignores input past 72 bytes, and an unbounded field lets a caller
    // make us hash megabytes per request on an anonymous route.
    [Required]
    [StringLength(200)]
    public string Password { get; set; } = "";
}

public record LoginResponse(string Token, DateTime ExpiresAt, string DisplayName, string Role);

public record MeResponse(Guid Id, string FirstName, string LastName, string Email);
