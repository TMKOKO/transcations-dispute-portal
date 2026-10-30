namespace DisputePortal.Api.Auth;

/// <summary>Bound from the <c>Agent</c> configuration section.</summary>
public class AgentOptions
{
    public const string SectionName = "Agent";

    /// <summary>
    /// The demo agent login. Off, the agents table is ignored at sign-in and a live agent session
    /// ends, so nobody can reach the agent endpoints. On only in local compose: the seeded agent
    /// shares the published demo password.
    /// </summary>
    public bool Enabled { get; set; }
}
