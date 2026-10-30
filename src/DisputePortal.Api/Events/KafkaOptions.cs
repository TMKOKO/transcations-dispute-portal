namespace DisputePortal.Api.Events;

public sealed class KafkaOptions
{
    public const string SectionName = "Kafka";

    /// <summary>
    /// Off: no publisher or consumer runs. Outbox rows are still written, and go out once it is on.
    /// Off in tests that do not need a broker.
    /// </summary>
    public bool Enabled { get; set; }

    public string BootstrapServers { get; set; } = "localhost:9094";

    /// <summary>Consumer group for back-office decisions. Every API instance joins it, so each decision is applied once.</summary>
    public string GroupId { get; set; } = "dispute-portal-api";
}
