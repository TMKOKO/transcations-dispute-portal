namespace DisputePortal.Api.Services;

public static class DisputeNumber
{
    /// <summary>
    /// Customer-facing case reference, e.g. <c>DSP-2026-001042</c>. The year is the year the dispute
    /// was raised (UTC); the counter comes from a database sequence, so it is unique across years too.
    /// </summary>
    public static string Format(int year, long sequence) => $"DSP-{year:D4}-{sequence:D6}";
}
