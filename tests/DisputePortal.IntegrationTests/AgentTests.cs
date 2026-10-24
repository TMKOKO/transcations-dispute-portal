using System.Net;
using System.Net.Http.Json;
using DisputePortal.Api.DTOs;
using DisputePortal.Api.Models;
using DisputePortal.Api.Services;
using DisputePortal.IntegrationTests.Infrastructure;

namespace DisputePortal.IntegrationTests;

[Collection(ApiCollection.Name)]
public class AgentTests : IAsyncLifetime
{
    private readonly ApiFactory _api;

    public AgentTests(PostgresFixture db) => _api = new ApiFactory(db);

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _api.DisposeAsync().AsTask();

    private async Task<(HttpClient Customer, HttpClient Agent, DisputeDetailResponse Dispute, Guid TransactionId)> ArrangeAsync()
    {
        var c = await _api.CreateCustomerAsync();
        var tx = await _api.CreateTransactionAsync(c.Id);
        var customer = await _api.CustomerClientAsync(c);
        var agent = await _api.AgentClientAsync(await _api.CreateAgentAsync());

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/disputes")
        {
            Content = JsonContent.Create(new
            {
                transactionId = tx.Id,
                reason = "Unauthorised",
                description = "I do not recognise this card payment at all.",
                disputedAmount = 100m,
            })
        };
        request.Headers.Add("Idempotency-Key", $"key-{Guid.NewGuid():N}");
        var dispute = await (await customer.SendAsync(request)).ReadAsync<DisputeDetailResponse>();

        return (customer, agent, dispute, tx.Id);
    }

    private static Task<HttpResponseMessage> SetStatus(HttpClient client, Guid id, string status, string? note = null) =>
        client.PatchAsJsonAsync($"/api/agent/disputes/{id}/status", new { status, note });

    [Fact]
    public async Task Agent_review_and_resolution_shows_on_the_customers_timeline()
    {
        var (customer, agent, dispute, txId) = await ArrangeAsync();

        Assert.Equal(HttpStatusCode.OK, (await SetStatus(agent, dispute.Id, "UnderReview")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SetStatus(agent, dispute.Id, "Resolved", "Refund credited.")).StatusCode);

        var seen = await (await customer.GetAsync($"/api/disputes/{dispute.Id}")).ReadAsync<DisputeDetailResponse>();
        Assert.Equal(DisputeStatus.Resolved, seen.Status);
        Assert.Equal([DisputeStatus.Submitted, DisputeStatus.UnderReview, DisputeStatus.Resolved],
            seen.Timeline.Select(t => t.ToStatus));
        Assert.Equal("agent", seen.Timeline[^1].ChangedBy);
        Assert.Equal("Refund credited.", seen.Timeline[^1].Note);
        Assert.False(seen.CanWithdraw);

        // Once resolved, the transaction cannot be disputed again.
        var tx = await (await customer.GetAsync($"/api/transactions/{txId}")).ReadAsync<TransactionResponse>();
        Assert.Equal(IneligibleReason.AlreadyResolved, tx.IneligibleCode);
    }

    [Fact]
    public async Task Resolving_or_rejecting_requires_a_note()
    {
        var (_, agent, dispute, _) = await ArrangeAsync();

        var response = await SetStatus(agent, dispute.Id, "Rejected");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("note_required", await response.ProblemCodeAsync());
    }

    [Fact]
    public async Task Illegal_transitions_are_409()
    {
        var (_, agent, dispute, _) = await ArrangeAsync();

        // Submitted -> Resolved skips review.
        var response = await SetStatus(agent, dispute.Id, "Resolved", "Skipping review");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("invalid_transition", await response.ProblemCodeAsync());
    }

    [Fact]
    public async Task Agents_cannot_withdraw_on_the_customers_behalf()
    {
        var (_, agent, dispute, _) = await ArrangeAsync();

        var response = await SetStatus(agent, dispute.Id, "Withdrawn");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("agent_cannot_withdraw", await response.ProblemCodeAsync());
    }

    [Fact]
    public async Task Roles_are_enforced_in_both_directions()
    {
        var (customer, agent, dispute, _) = await ArrangeAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await SetStatus(customer, dispute.Id, "UnderReview")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.GetAsync("/api/agent/disputes")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await agent.GetAsync("/api/transactions")).StatusCode);
    }

    [Fact]
    public async Task Unknown_dispute_is_404()
    {
        var (_, agent, _, _) = await ArrangeAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await SetStatus(agent, Guid.NewGuid(), "UnderReview")).StatusCode);
    }
}
