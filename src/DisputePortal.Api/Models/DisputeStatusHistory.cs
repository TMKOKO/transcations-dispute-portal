namespace DisputePortal.Api.Models;

/// <summary>
/// One row per status change, appended by <see cref="Dispute"/> itself — never written directly —
/// so the timeline the customer sees cannot drift from the status column.
/// </summary>
public class DisputeStatusHistory
{
    public long Id { get; set; }

    public Guid DisputeId { get; set; }
    public Dispute? Dispute { get; set; }

    // Null only on the first row, when the dispute is raised.
    public DisputeStatus? FromStatus { get; set; }
    public DisputeStatus ToStatus { get; set; }

    public string? Note { get; set; }
    public string ChangedBy { get; set; } = string.Empty;   // DisputeActor.*
    public DateTime ChangedAt { get; set; }
}

public static class DisputeActor
{
    public const string Customer = "customer";
    public const string Agent    = "agent";
    public const string System   = "system";
}
