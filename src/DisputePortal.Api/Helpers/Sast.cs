namespace DisputePortal.Api.Helpers;

/// <summary>
/// South Africa Standard Time is permanently UTC+2 (no DST), so a fixed offset is exact and avoids
/// depending on the container having tzdata. Customers filter by SAST calendar dates; storage is UTC.
/// </summary>
public static class Sast
{
    public static readonly TimeSpan Offset = TimeSpan.FromHours(2);

    /// <summary>The UTC instant at which <paramref name="date"/> begins in SAST.</summary>
    public static DateTime StartOfDayUtc(DateOnly date) =>
        DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue) - Offset, DateTimeKind.Utc);
}
