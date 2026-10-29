using System.Net;
using System.Net.Sockets;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using DisputePortal.Api.Events;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace DisputePortal.IntegrationTests.Infrastructure;

/// <summary>
/// A single-node Kafka (the same apache/kafka image as docker-compose.yml), only for the test class
/// that needs it, so every other test runs without the extra memory.
/// </summary>
public sealed class KafkaFixture : IAsyncLifetime
{
    private readonly int _port = FreePort();
    private readonly IContainer _kafka;

    public KafkaFixture()
    {
        // Kafka tells clients to reconnect to its advertised address, so the host port must be known
        // before the container starts: a fixed binding to a port that was free a moment ago.
        _kafka = new ContainerBuilder()
            .WithImage("apache/kafka:3.8.0")
            .WithPortBinding(_port, 9092)
            .WithEnvironment("KAFKA_NODE_ID", "1")
            .WithEnvironment("KAFKA_PROCESS_ROLES", "broker,controller")
            .WithEnvironment("KAFKA_LISTENERS", "PLAINTEXT://:9092,CONTROLLER://:9093")
            .WithEnvironment("KAFKA_ADVERTISED_LISTENERS", $"PLAINTEXT://localhost:{_port}")
            .WithEnvironment("KAFKA_LISTENER_SECURITY_PROTOCOL_MAP", "CONTROLLER:PLAINTEXT,PLAINTEXT:PLAINTEXT")
            .WithEnvironment("KAFKA_CONTROLLER_LISTENER_NAMES", "CONTROLLER")
            .WithEnvironment("KAFKA_CONTROLLER_QUORUM_VOTERS", "1@localhost:9093")
            .WithEnvironment("KAFKA_OFFSETS_TOPIC_REPLICATION_FACTOR", "1")
            .WithEnvironment("KAFKA_TRANSACTION_STATE_LOG_REPLICATION_FACTOR", "1")
            .WithEnvironment("KAFKA_TRANSACTION_STATE_LOG_MIN_ISR", "1")
            .WithEnvironment("KAFKA_GROUP_INITIAL_REBALANCE_DELAY_MS", "0")
            .WithEnvironment("KAFKA_HEAP_OPTS", "-Xmx256m -Xms256m")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Kafka Server started"))
            .Build();
    }

    public string BootstrapServers => $"localhost:{_port}";

    public async Task InitializeAsync()
    {
        await _kafka.StartAsync();

        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = BootstrapServers }).Build();
        string[] topics =
        [
            DisputeTopics.Submitted, DisputeTopics.Withdrawn,
            DisputeTopics.UnderReview, DisputeTopics.RefundIssued, DisputeTopics.Rejected,
            DisputeTopics.DecisionsDeadLetter,
        ];
        await admin.CreateTopicsAsync(topics.Select(t => new TopicSpecification { Name = t, NumPartitions = 3, ReplicationFactor = 1 }));
    }

    public Task DisposeAsync() => _kafka.DisposeAsync().AsTask();

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
