namespace DisputePortal.Api.Services;

/// <summary>Bound from the <c>Disputes</c> configuration section.</summary>
public class DisputeOptions
{
    public const string SectionName = "Disputes";

    /// <summary>
    /// How many days after the transaction date a customer may still raise a dispute. 120 days is
    /// the common card-scheme chargeback window; a transaction exactly this old is still eligible.
    /// </summary>
    public int WindowDays { get; set; } = 120;
}
