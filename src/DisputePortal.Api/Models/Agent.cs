namespace DisputePortal.Api.Models;

/// <summary>
/// A back-office staff member who reviews disputes. Kept separate from <see cref="Customer"/>
/// so a customer row can never be promoted into a reviewer by flipping a column.
/// </summary>
public class Agent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string DisplayName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
