using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DisputePortal.Api.DTOs;
using DisputePortal.Api.Events;
using DisputePortal.Api.Models;
using DisputePortal.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DisputePortal.IntegrationTests;

/// <summary>The demo agent: its login, its reach, and decisions that go out through the outbox (Kafka is off).</summary>
[Collection(ApiCollection.Name)]
public class AgentTests : IAsyncLifetime
{
    private const string Description = "Private details the customer typed about this charge.";
    private const string Note = "A note the agent typed for the customer.";

    private readonly ContainersFixture _containers;
    private readonly ApiFactory _api;

    public AgentTests(ContainersFixture db)
    {
        _containers = db;
        _api = new ApiFactory(db, agentEnabled: true);
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _api.DisposeAsync().AsTask();

    private async Task<(Customer Customer, HttpClient Client, Guid DisputeId)> RaisedDisputeAsync()
    {
        var customer = await _api.CreateCustomerAsync();
        var tx = await _api.CreateTransactionAsync(customer.Id, merchant: "Agent Test Merchant");
        var client = await _api.CustomerClientAsync(customer);

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/disputes")
        {
            Content = JsonContent.Create(new { transactionId = tx.Id, reason = "DuplicateCharge", description = Description, disputedAmount = 100m }),
        };
        request.Headers.Add("Idempotency-Key", $"key-{Guid.NewGuid():N}");
        var raised = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, raised.StatusCode);
        return (customer, client, (await raised.ReadAsync<DisputeDetailResponse>()).Id);
    }

    private static Task<HttpResponseMessage> DecideAsync(HttpClient agent, Guid disputeId, string decision, string? note = null) =>
        agent.PostAsJsonAsync("/api/agent/disputes/decision", new { disputeId, decision, note });

    private Task<List<OutboxMessage>> DecisionsForAsync(Guid disputeId) => _api.WithDbAsync(db => db.OutboxMessages.AsNoTracking()
        .Where(m => m.Key == disputeId.ToString() && m.Topic != DisputeTopics.Submitted && m.Topic != DisputeTopics.Withdrawn)
        .OrderBy(m => m.Id).ToListAsync());

    private Task<DisputeStatus> StatusOfAsync(Guid disputeId) => _api.WithDbAsync(db =>
        db.Disputes.AsNoTracking().Where(d => d.Id == disputeId).Select(d => d.Status).SingleAsync());

    /// <summary>What the consumer would do: hands the outbox row to the handler.</summary>
    private Task<DecisionResult> ConsumeAsync(OutboxMessage message) =>
        _api.WithServiceAsync<DecisionHandler, DecisionResult>(h => h.HandleAsync(message.Topic, message.Payload, CancellationToken.None));

    // --- Login ---

    [Fact]
    public async Task An_agent_signs_in_with_the_agent_role_and_can_sign_out()
    {
        var agent = await _api.CreateAgentAsync();
        var client = _api.CreateClient();

        var response = await ApiFactory.LoginAsync(client, agent);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.ReadJsonAsync();
        Assert.Equal("agent", body.GetProperty("role").GetString());
        Assert.Equal(agent.DisplayName, body.GetProperty("displayName").GetString());
        Assert.False(body.TryGetProperty("token", out _));
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync("/api/agent/session")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/agent/session")).StatusCode);
    }

    [Fact]
    public async Task A_wrong_agent_password_gets_the_same_401_as_anyone_else()
    {
        var agent = await _api.CreateAgentAsync();

        var response = await _api.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = agent.Email, password = "not-the-password-123" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("invalid_credentials", await response.ProblemCodeAsync());
        Assert.Contains(_api.Logs.GetSnapshot(), r => r.Message.Contains("Agent login failed") && r.Message.Contains(agent.Id.ToString()));
    }

    [Fact]
    public async Task With_the_flag_off_an_agent_cannot_sign_in()
    {
        var agent = await _api.CreateAgentAsync();
        await using var off = new ApiFactory(_containers);

        var response = await ApiFactory.LoginAsync(off.CreateClient(), agent);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("invalid_credentials", await response.ProblemCodeAsync());
    }

    [Fact]
    public async Task Turning_the_flag_off_ends_agent_sessions_already_in_redis()
    {
        var agent = await _api.CreateAgentAsync();
        var sessionId = ApiFactory.SessionIdFrom(await ApiFactory.LoginAsync(_api.CreateClient(), agent));
        await using var off = new ApiFactory(_containers);

        var response = await off.ClientWithSession(sessionId).GetAsync("/api/agent/disputes");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // --- Reach ---

    [Theory]
    [InlineData("/api/me")]
    [InlineData("/api/transactions")]
    [InlineData("/api/disputes")]
    [InlineData("/api/dispute-reasons")]
    public async Task An_agent_cannot_use_customer_endpoints(string path)
    {
        var client = await _api.AgentClientAsync(await _api.CreateAgentAsync());

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("forbidden", await response.ProblemCodeAsync());
    }

    [Fact]
    public async Task An_agent_cannot_raise_or_withdraw_a_dispute()
    {
        var (_, _, disputeId) = await RaisedDisputeAsync();
        var client = await _api.AgentClientAsync(await _api.CreateAgentAsync());

        var withdraw = await client.PostAsJsonAsync("/api/disputes/withdraw", new { disputeId });

        Assert.Equal(HttpStatusCode.Forbidden, withdraw.StatusCode);
        Assert.Equal(DisputeStatus.Submitted, await StatusOfAsync(disputeId));
    }

    [Fact]
    public async Task A_customer_cannot_use_agent_endpoints_and_nobody_can_without_a_session()
    {
        var (_, customer, disputeId) = await RaisedDisputeAsync();

        var list = await customer.GetAsync("/api/agent/disputes");
        var decide = await DecideAsync(customer, disputeId, "Rejected");
        var anonymous = await _api.CreateClient().GetAsync("/api/agent/disputes");

        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        Assert.Equal("forbidden", await list.ProblemCodeAsync());
        Assert.Equal(HttpStatusCode.Forbidden, decide.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Empty(await DecisionsForAsync(disputeId));
    }

    // --- List ---

    [Fact]
    public async Task The_list_spans_customers_and_leaves_out_private_text()
    {
        var (_, _, first) = await RaisedDisputeAsync();
        var (_, _, second) = await RaisedDisputeAsync();
        var client = await _api.AgentClientAsync(await _api.CreateAgentAsync());

        var response = await client.GetAsync("/api/agent/disputes?status=Submitted&pageSize=100");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        var page = JsonSerializer.Deserialize<PagedResponse<AgentDisputeResponse>>(raw, Json.Options)!;
        Assert.Contains(page.Items, d => d.Id == first);
        var row = Assert.Single(page.Items, d => d.Id == second);
        Assert.Equal(DisputeStatus.Submitted, row.Status);
        Assert.Equal("Agent Test Merchant", row.MerchantName);
        Assert.Equal(100m, row.DisputedAmount);
        Assert.All(page.Items, d => Assert.Equal(DisputeStatus.Submitted, d.Status));
        Assert.DoesNotContain(Description, raw);
        Assert.DoesNotContain("description", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("email", raw, StringComparison.OrdinalIgnoreCase);
    }

    // --- Decisions ---

    [Theory]
    [InlineData("UnderReview", DisputeTopics.UnderReview, DisputeEventTypes.UnderReview)]
    [InlineData("RefundIssued", DisputeTopics.RefundIssued, DisputeEventTypes.RefundIssued)]
    [InlineData("Rejected", DisputeTopics.Rejected, DisputeEventTypes.Rejected)]
    public async Task A_decision_is_queued_for_its_topic_and_changes_nothing_yet(string decision, string topic, string eventType)
    {
        var (_, _, disputeId) = await RaisedDisputeAsync();
        var client = await _api.AgentClientAsync(await _api.CreateAgentAsync());

        var response = await DecideAsync(client, disputeId, decision, $"  {Note}  ");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Null(response.Headers.Location);
        var body = await response.ReadAsync<AgentDecisionResponse>();
        Assert.Equal(topic, body.Topic);
        Assert.Equal(eventType, body.EventType);

        var message = Assert.Single(await DecisionsForAsync(disputeId));
        Assert.Equal(topic, message.Topic);
        Assert.Equal(disputeId.ToString(), message.Key);
        Assert.Equal(body.EventId, message.EventId);
        var payload = JsonDocument.Parse(message.Payload).RootElement;
        Assert.Equal(Note, payload.GetProperty("note").GetString());
        Assert.DoesNotContain(Description, message.Payload);

        // Only the consumer moves a dispute.
        Assert.Equal(DisputeStatus.Submitted, await StatusOfAsync(disputeId));
    }

    [Fact]
    public async Task A_blank_note_is_no_note()
    {
        var (_, _, disputeId) = await RaisedDisputeAsync();
        var client = await _api.AgentClientAsync(await _api.CreateAgentAsync());

        Assert.Equal(HttpStatusCode.Accepted, (await DecideAsync(client, disputeId, "UnderReview", "   ")).StatusCode);

        var message = Assert.Single(await DecisionsForAsync(disputeId));
        Assert.False(JsonDocument.Parse(message.Payload).RootElement.TryGetProperty("note", out _));
    }

    [Fact]
    public async Task The_consumer_applies_a_valid_decision_as_an_agent_on_the_timeline()
    {
        var (_, customer, disputeId) = await RaisedDisputeAsync();
        var client = await _api.AgentClientAsync(await _api.CreateAgentAsync());

        await DecideAsync(client, disputeId, "UnderReview", Note);
        var result = await ConsumeAsync(Assert.Single(await DecisionsForAsync(disputeId)));

        Assert.Equal(DecisionOutcome.Applied, result.Outcome);
        var detail = await (await customer.PostAsJsonAsync("/api/disputes/lookup", new { disputeId })).ReadAsync<DisputeDetailResponse>();
        Assert.Equal(DisputeStatus.UnderReview, detail.Status);
        var entry = detail.Timeline[^1];
        Assert.Equal(DisputeActor.Agent, entry.ChangedBy);
        Assert.Equal(Note, entry.Note);
    }

    [Fact]
    public async Task A_move_that_is_not_allowed_yet_is_still_sent_and_the_consumer_dead_letters_it()
    {
        var (_, _, disputeId) = await RaisedDisputeAsync();
        var client = await _api.AgentClientAsync(await _api.CreateAgentAsync());

        var response = await DecideAsync(client, disputeId, "RefundIssued");
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var result = await ConsumeAsync(Assert.Single(await DecisionsForAsync(disputeId)));

        Assert.Equal(DecisionOutcome.DeadLetter, result.Outcome);
        Assert.Equal("transition_not_allowed", result.Reason);
        Assert.Equal(DisputeStatus.Submitted, await StatusOfAsync(disputeId));
    }

    [Fact]
    public async Task A_decision_on_a_closed_dispute_is_still_sent_and_the_consumer_skips_it_as_stale()
    {
        var (_, _, disputeId) = await RaisedDisputeAsync();
        var client = await _api.AgentClientAsync(await _api.CreateAgentAsync());

        await DecideAsync(client, disputeId, "Rejected");
        Assert.Equal(DecisionOutcome.Applied, (await ConsumeAsync(Assert.Single(await DecisionsForAsync(disputeId)))).Outcome);

        Assert.Equal(HttpStatusCode.Accepted, (await DecideAsync(client, disputeId, "UnderReview")).StatusCode);
        var late = (await DecisionsForAsync(disputeId))[^1];
        var result = await ConsumeAsync(late);

        Assert.Equal(DecisionOutcome.Stale, result.Outcome);
        Assert.Equal(DisputeStatus.Rejected, await StatusOfAsync(disputeId));
    }

    [Fact]
    public async Task A_decision_on_an_unknown_dispute_is_404_and_queues_nothing()
    {
        var client = await _api.AgentClientAsync(await _api.CreateAgentAsync());
        var unknown = Guid.NewGuid();

        var response = await DecideAsync(client, unknown, "UnderReview");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("dispute_not_found", await response.ProblemCodeAsync());
        Assert.Empty(await DecisionsForAsync(unknown));
    }

    [Theory]
    [InlineData("Resolved")]    // a status, not a decision
    [InlineData("Nonsense")]
    public async Task An_invalid_decision_is_400(string decision)
    {
        var (_, _, disputeId) = await RaisedDisputeAsync();
        var client = await _api.AgentClientAsync(await _api.CreateAgentAsync());

        Assert.Equal(HttpStatusCode.BadRequest, (await DecideAsync(client, disputeId, decision)).StatusCode);
        Assert.Empty(await DecisionsForAsync(disputeId));
    }

    [Theory]
    [InlineData("Bad\u0000note")]
    [InlineData("Bad‮note")]
    public async Task A_note_with_invisible_characters_is_400(string note)
    {
        var (_, _, disputeId) = await RaisedDisputeAsync();
        var client = await _api.AgentClientAsync(await _api.CreateAgentAsync());

        Assert.Equal(HttpStatusCode.BadRequest, (await DecideAsync(client, disputeId, "UnderReview", note)).StatusCode);
        Assert.Empty(await DecisionsForAsync(disputeId));
    }

    [Fact]
    public async Task A_note_over_the_limit_is_400()
    {
        var (_, _, disputeId) = await RaisedDisputeAsync();
        var client = await _api.AgentClientAsync(await _api.CreateAgentAsync());

        var response = await DecideAsync(client, disputeId, "UnderReview", new string('a', AgentDecisionRequest.NoteMaxLength + 1));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Ids_travel_in_the_body_not_the_url()
    {
        var (_, _, disputeId) = await RaisedDisputeAsync();
        var client = await _api.AgentClientAsync(await _api.CreateAgentAsync());

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/agent/disputes/{disputeId}")).StatusCode);
    }

    // --- Logging ---

    [Fact]
    public async Task The_note_and_the_email_are_never_logged()
    {
        var (_, _, disputeId) = await RaisedDisputeAsync();
        var agent = await _api.CreateAgentAsync();
        var client = await _api.AgentClientAsync(agent);

        await DecideAsync(client, disputeId, "UnderReview", Note);

        var logs = _api.Logs.GetSnapshot();
        Assert.Contains(logs, r => r.Message.StartsWith($"Agent {agent.Id} signed in"));
        Assert.Contains(logs, r => r.Message.StartsWith($"Agent {agent.Id} queued"));
        Assert.DoesNotContain(logs, r => r.Message.Contains(Note)
                                      || r.Message.Contains(agent.Email, StringComparison.OrdinalIgnoreCase));
    }
}
