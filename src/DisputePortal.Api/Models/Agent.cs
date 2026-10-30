namespace DisputePortal.Api.Models;

/// <summary>
/// Plays the bank's back office in a demo (see <see cref="Auth.AgentOptions"/>). Not a kind of
/// customer: an agent's session has no customer id, so customer endpoints refuse it.
/// </summary>
public class Agent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string DisplayName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
