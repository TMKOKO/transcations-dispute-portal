using System.Net;
using System.Net.Http.Json;
using DisputePortal.Api.DTOs;
using DisputePortal.Api.Events;
using DisputePortal.Api.Models;
using DisputePortal.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DisputePortal.IntegrationTests;

/// <summary>A competing write lands between a service's check and its save, every time (<see cref="BeforeSave"/>).</summary>
[Collection(ApiCollection.Name)]
public class RaceTests : IAsyncLifetime
{
    private readonly BeforeSave _beforeSave = new();
    private readonly ApiFactory _api;

    public RaceTests(ContainersFixture db) => _api = new ApiFactory(db, configureServices: _beforeSave.AddTo);

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _api.DisposeAsync().AsTask();

    private async Task<(HttpClient Client, Transaction Tx)> CustomerWithTransactionAsync()
    {
        var customer = await _api.CreateCustomerAsync();
        var tx = await _api.CreateTransactionAsync(customer.Id);
        return (await _api.CustomerClientAsync(customer), tx);
    }

    private async Task<(HttpClient Client, Guid DisputeId)> RaisedDisputeAsync()
    {
        var (client, tx) = await CustomerWithTransactionAsync();
        var raised = await EventsTests.RaiseAsync(client, tx.Id);
        return (client, (await raised.ReadAsync<DisputeDetailResponse>()).Id);
    }

    private void BeforeInsertingADispute(Func<Task> competingWrite) =>
        _beforeSave.Arm(e => e.Entity is Dispute && e.State == EntityState.Added, competingWrite);

    private Task<DecisionResult> HandleAsync(string topic, string payload) =>
        _api.WithServiceAsync<DecisionHandler, DecisionResult>(h => h.HandleAsync(topic, payload, CancellationToken.None));

    private static string UnderReview(Guid disputeId) => new DisputeEvent
    {
        EventId    = Guid.NewGuid(),
        EventType  = DisputeEventTypes.UnderReview,
        OccurredAt = DateTimeOffset.UtcNow,
        DisputeId  = disputeId,
    }.ToJson();

    private Task<Dispute> DisputeAsync(Guid id) => _api.WithDbAsync(db =>
        db.Disputes.AsNoTracking().Include(d => d.History).SingleAsync(d => d.Id == id));

    // --- Raising ---

    [Fact]
    public async Task A_double_submit_that_races_returns_the_winner_as_a_replay()
    {
        var (client, tx) = await CustomerWithTransactionAsync();
        HttpResponseMessage? winner = null;
        BeforeInsertingADispute(async () => winner = await EventsTests.RaiseAsync(client, tx.Id, key: "double-click"));

        var loser = await EventsTests.RaiseAsync(client, tx.Id, key: "double-click");

        Assert.Equal(HttpStatusCode.Created, winner!.StatusCode);
        Assert.Equal(HttpStatusCode.OK, loser.StatusCode);
        Assert.Equal("true", loser.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal((await winner.ReadAsync<DisputeDetailResponse>()).Id, (await loser.ReadAsync<DisputeDetailResponse>()).Id);
        Assert.Equal(1, await _api.WithDbAsync(db => db.Disputes.CountAsync(d => d.TransactionId == tx.Id)));
    }

    [Fact]
    public async Task Racing_on_one_transaction_with_different_keys_is_a_conflict()
    {
        var (client, tx) = await CustomerWithTransactionAsync();
        BeforeInsertingADispute(() => EventsTests.RaiseAsync(client, tx.Id));

        var loser = await EventsTests.RaiseAsync(client, tx.Id);

        Assert.Equal(HttpStatusCode.Conflict, loser.StatusCode);
        Assert.Equal("dispute_already_open", await loser.ProblemCodeAsync());
        Assert.Equal(1, await _api.WithDbAsync(db => db.Disputes.CountAsync(d => d.TransactionId == tx.Id)));
    }

    // --- Withdrawing ---

    [Fact]
    public async Task A_withdraw_that_loses_to_a_concurrent_change_is_a_conflict_and_is_not_applied()
    {
        var (client, disputeId) = await RaisedDisputeAsync();
        _beforeSave.Arm(e => e.Entity is Dispute, () => HandleAsync(DisputeTopics.UnderReview, UnderReview(disputeId)));

        var response = await client.PostAsJsonAsync("/api/disputes/withdraw", new { disputeId });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("concurrent_update", await response.ProblemCodeAsync());
        var dispute = await DisputeAsync(disputeId);
        Assert.Equal(DisputeStatus.UnderReview, dispute.Status);
        Assert.DoesNotContain(dispute.History, h => h.ToStatus == DisputeStatus.Withdrawn);
    }

    // --- Decisions ---

    [Fact]
    public async Task A_decision_that_loses_to_a_withdraw_decides_again_and_is_stale()
    {
        var (client, disputeId) = await RaisedDisputeAsync();
        _beforeSave.Arm(e => e.Entity is Dispute, () => client.PostAsJsonAsync("/api/disputes/withdraw", new { disputeId }));

        var result = await HandleAsync(DisputeTopics.UnderReview, UnderReview(disputeId));

        Assert.Equal(DecisionOutcome.Stale, result.Outcome);
        var dispute = await DisputeAsync(disputeId);
        Assert.Equal(DisputeStatus.Withdrawn, dispute.Status);
        Assert.DoesNotContain(dispute.History, h => h.ToStatus == DisputeStatus.UnderReview);
    }

    [Fact]
    public async Task A_decision_that_loses_every_retry_throws_so_the_consumer_tries_again_later()
    {
        var (_, disputeId) = await RaisedDisputeAsync();
        var changes = 0;
        void ChangeBeforeTheNextSave() => _beforeSave.Arm(e => e.Entity is Dispute, async () =>
        {
            if (++changes < 3) ChangeBeforeTheNextSave();
            await _api.WithDbAsync(db => db.Disputes.Where(d => d.Id == disputeId)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.UpdatedAt, DateTime.UtcNow)));
        });
        ChangeBeforeTheNextSave();

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => HandleAsync(DisputeTopics.UnderReview, UnderReview(disputeId)));

        Assert.Equal(3, changes);
        Assert.Equal(DisputeStatus.Submitted, (await DisputeAsync(disputeId)).Status);
    }

    [Fact]
    public async Task Two_consumers_handling_one_event_record_it_once()
    {
        // Withdrawn, so the decision is stale and only writes its inbox row: the primary key is the
        // one thing left to catch the second consumer.
        var (client, disputeId) = await RaisedDisputeAsync();
        await client.PostAsJsonAsync("/api/disputes/withdraw", new { disputeId });
        var payload = UnderReview(disputeId);
        DecisionResult? other = null;
        _beforeSave.Arm(e => e.Entity is InboxMessage, async () => other = await HandleAsync(DisputeTopics.UnderReview, payload));

        var result = await HandleAsync(DisputeTopics.UnderReview, payload);

        Assert.Equal(DecisionOutcome.Stale, other!.Outcome);
        Assert.Equal(DecisionOutcome.Duplicate, result.Outcome);
        Assert.Equal(1, await _api.WithDbAsync(db => db.InboxMessages.CountAsync(i => i.DisputeId == disputeId)));
    }
}
