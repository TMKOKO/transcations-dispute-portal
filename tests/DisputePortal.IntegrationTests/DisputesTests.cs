using System.Net;
using System.Net.Http.Json;
using DisputePortal.Api.DTOs;
using DisputePortal.Api.Models;
using DisputePortal.Api.Services;
using DisputePortal.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DisputePortal.IntegrationTests;

[Collection(ApiCollection.Name)]
public class DisputesTests : IAsyncLifetime
{
    private readonly ApiFactory _api;

    public DisputesTests(PostgresFixture db) => _api = new ApiFactory(db);

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _api.DisposeAsync().AsTask();

    private static object Body(Guid transactionId, decimal amount = 100m, string reason = "DuplicateCharge") => new
    {
        transactionId,
        reason,
        description = "I was charged twice for a single purchase.",
        disputedAmount = amount,
    };

    private static Task<HttpResponseMessage> RaiseAsync(HttpClient client, object body, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/disputes") { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", key ?? $"key-{Guid.NewGuid():N}");
        return client.SendAsync(request);
    }

    [Fact]
    public async Task Raising_a_dispute_returns_201_with_location_and_first_timeline_row()
    {
        var c = await _api.CreateCustomerAsync();
        var tx = await _api.CreateTransactionAsync(c.Id, amount: 499.99m);
        var client = await _api.CustomerClientAsync(c);

        var response = await RaiseAsync(client, Body(tx.Id, 499.99m));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dispute = await response.ReadAsync<DisputeDetailResponse>();
        Assert.Equal(response.Headers.Location!.AbsolutePath, $"/api/disputes/{dispute.Id}", ignoreCase: true);
        Assert.Matches(@"^DSP-\d{4}-\d{6}$", dispute.DisputeNumber);
        Assert.Equal(DisputeStatus.Submitted, dispute.Status);
        Assert.Equal(499.99m, dispute.DisputedAmount);
        Assert.True(dispute.CanWithdraw);
        Assert.Equal(tx.Id, dispute.Transaction.Id);

        var row = Assert.Single(dispute.Timeline);
        Assert.Null(row.FromStatus);
        Assert.Equal("customer", row.ChangedBy);

        // The transaction now reports the open dispute and is no longer offered for dispute.
        var txAfter = await (await client.GetAsync($"/api/transactions/{tx.Id}")).ReadAsync<TransactionResponse>();
        Assert.False(txAfter.CanDispute);
        Assert.Equal(IneligibleReason.ActiveDispute, txAfter.IneligibleCode);
        Assert.Equal(dispute.Id, txAfter.LatestDispute!.Id);
    }

    [Fact]
    public async Task Replaying_the_same_idempotency_key_returns_the_original_without_a_duplicate()
    {
        var c = await _api.CreateCustomerAsync();
        var tx = await _api.CreateTransactionAsync(c.Id);
        var client = await _api.CustomerClientAsync(c);

        var first = await RaiseAsync(client, Body(tx.Id), key: "same-key-123");
        var second = await RaiseAsync(client, Body(tx.Id), key: "same-key-123");

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("true", second.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal((await first.ReadAsync<DisputeDetailResponse>()).Id, (await second.ReadAsync<DisputeDetailResponse>()).Id);

        var count = await _api.WithDbAsync(db => db.Disputes.CountAsync(d => d.TransactionId == tx.Id));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Idempotency_keys_are_scoped_per_customer()
    {
        var a = await _api.CreateCustomerAsync();
        var b = await _api.CreateCustomerAsync();
        var aTx = await _api.CreateTransactionAsync(a.Id);
        var bTx = await _api.CreateTransactionAsync(b.Id);

        var aResponse = await RaiseAsync(await _api.CustomerClientAsync(a), Body(aTx.Id), key: "shared-key-1");
        var bResponse = await RaiseAsync(await _api.CustomerClientAsync(b), Body(bTx.Id), key: "shared-key-1");

        Assert.Equal(HttpStatusCode.Created, aResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, bResponse.StatusCode);
    }

    [Fact]
    public async Task Reusing_a_key_for_a_different_transaction_is_409()
    {
        var c = await _api.CreateCustomerAsync();
        var tx1 = await _api.CreateTransactionAsync(c.Id);
        var tx2 = await _api.CreateTransactionAsync(c.Id);
        var client = await _api.CustomerClientAsync(c);

        await RaiseAsync(client, Body(tx1.Id), key: "reused-key-1");
        var response = await RaiseAsync(client, Body(tx2.Id), key: "reused-key-1");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("idempotency_key_reused", await response.ProblemCodeAsync());
    }

    [Fact]
    public async Task A_second_open_dispute_on_the_same_transaction_is_409()
    {
        var c = await _api.CreateCustomerAsync();
        var tx = await _api.CreateTransactionAsync(c.Id);
        var client = await _api.CustomerClientAsync(c);

        await RaiseAsync(client, Body(tx.Id));
        var second = await RaiseAsync(client, Body(tx.Id));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("dispute_already_open", await second.ProblemCodeAsync());
    }

    [Fact]
    public async Task Concurrent_submits_create_exactly_one_dispute()
    {
        // Distinct keys, so idempotency does not help: only the partial unique index stops these.
        var c = await _api.CreateCustomerAsync();
        var tx = await _api.CreateTransactionAsync(c.Id);
        var client = await _api.CustomerClientAsync(c);

        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => RaiseAsync(client, Body(tx.Id))));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.All(responses.Where(r => r.StatusCode != HttpStatusCode.Created),
            r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        Assert.Equal(1, await _api.WithDbAsync(db => db.Disputes.CountAsync(d => d.TransactionId == tx.Id)));
    }

    [Fact]
    public async Task Missing_or_malformed_idempotency_key_is_400()
    {
        var c = await _api.CreateCustomerAsync();
        var tx = await _api.CreateTransactionAsync(c.Id);
        var client = await _api.CustomerClientAsync(c);

        var missing = await client.PostAsJsonAsync("/api/disputes", Body(tx.Id));
        var tooShort = await RaiseAsync(client, Body(tx.Id), key: "short");
        var badChars = await RaiseAsync(client, Body(tx.Id), key: "has spaces in it");

        foreach (var r in new[] { missing, tooShort, badChars })
        {
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            Assert.Equal("idempotency_key_required", await r.ProblemCodeAsync());
        }
    }

    [Fact]
    public async Task Validation_failures_are_400_problem_details_keyed_by_json_field_name()
    {
        var c = await _api.CreateCustomerAsync();
        var tx = await _api.CreateTransactionAsync(c.Id);
        var client = await _api.CustomerClientAsync(c);

        var response = await RaiseAsync(client, new { transactionId = tx.Id, description = "too short", disputedAmount = 0 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var errors = (await response.ReadJsonAsync()).GetProperty("errors");
        Assert.True(errors.TryGetProperty("description", out _));
        Assert.True(errors.TryGetProperty("disputedAmount", out _));
        Assert.True(errors.TryGetProperty("reason", out _));
    }

    [Fact]
    public async Task Unknown_reason_is_400_without_leaking_type_names()
    {
        var c = await _api.CreateCustomerAsync();
        var tx = await _api.CreateTransactionAsync(c.Id);
        var client = await _api.CustomerClientAsync(c);

        var response = await RaiseAsync(client, Body(tx.Id, reason: "BecauseISaidSo"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("DisputePortal", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Disputing_more_than_the_transaction_amount_is_422()
    {
        var c = await _api.CreateCustomerAsync();
        var tx = await _api.CreateTransactionAsync(c.Id, amount: 100m);
        var client = await _api.CustomerClientAsync(c);

        var over = await RaiseAsync(client, Body(tx.Id, amount: 100.01m));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, over.StatusCode);
        Assert.Equal("amount_exceeds_transaction", await over.ProblemCodeAsync());

        // A partial amount is fine.
        Assert.Equal(HttpStatusCode.Created, (await RaiseAsync(client, Body(tx.Id, amount: 40m))).StatusCode);
    }

    [Theory]
    [InlineData(TransactionType.Credit, TransactionStatus.Settled, 5, "not_a_debit")]
    [InlineData(TransactionType.Debit, TransactionStatus.Reversed, 5, "transaction_reversed")]
    [InlineData(TransactionType.Debit, TransactionStatus.Pending, 0, "transaction_pending")]
    [InlineData(TransactionType.Debit, TransactionStatus.Settled, 121, "outside_dispute_window")]
    public async Task Ineligible_transactions_are_422_with_a_reason_code(
        TransactionType type, TransactionStatus status, int daysAgo, string code)
    {
        var c = await _api.CreateCustomerAsync();
        var tx = await _api.CreateTransactionAsync(c.Id, type: type, status: status, daysAgo: daysAgo);
        var client = await _api.CustomerClientAsync(c);

        var response = await RaiseAsync(client, Body(tx.Id));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(code, await response.ProblemCodeAsync());
    }

    [Fact]
    public async Task Customers_cannot_see_dispute_or_withdraw_each_others_disputes()
    {
        var a = await _api.CreateCustomerAsync();
        var b = await _api.CreateCustomerAsync();
        var aTx = await _api.CreateTransactionAsync(a.Id);
        var aClient = await _api.CustomerClientAsync(a);
        var bClient = await _api.CustomerClientAsync(b);

        var bOnATx = await RaiseAsync(bClient, Body(aTx.Id));
        Assert.Equal(HttpStatusCode.NotFound, bOnATx.StatusCode);

        var aDispute = await (await RaiseAsync(aClient, Body(aTx.Id))).ReadAsync<DisputeDetailResponse>();

        Assert.Equal(HttpStatusCode.NotFound, (await bClient.GetAsync($"/api/disputes/{aDispute.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bClient.PostAsync($"/api/disputes/{aDispute.Id}/withdraw", null)).StatusCode);
        var bList = await (await bClient.GetAsync("/api/disputes")).ReadAsync<PagedResponse<DisputeSummaryResponse>>();
        Assert.Empty(bList.Items);
    }

    [Fact]
    public async Task Withdrawing_closes_the_dispute_and_allows_a_new_one()
    {
        var c = await _api.CreateCustomerAsync();
        var tx = await _api.CreateTransactionAsync(c.Id);
        var client = await _api.CustomerClientAsync(c);
        var dispute = await (await RaiseAsync(client, Body(tx.Id))).ReadAsync<DisputeDetailResponse>();

        var withdrawn = await client.PostAsJsonAsync($"/api/disputes/{dispute.Id}/withdraw", new { note = "Merchant refunded me." });

        Assert.Equal(HttpStatusCode.OK, withdrawn.StatusCode);
        var body = await withdrawn.ReadAsync<DisputeDetailResponse>();
        Assert.Equal(DisputeStatus.Withdrawn, body.Status);
        Assert.False(body.CanWithdraw);
        Assert.NotNull(body.ResolvedAt);
        Assert.Equal("Merchant refunded me.", body.Timeline[^1].Note);

        var again = await client.PostAsync($"/api/disputes/{dispute.Id}/withdraw", null);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("invalid_transition", await again.ProblemCodeAsync());

        // The partial index only covers open disputes, so a fresh one is allowed.
        Assert.Equal(HttpStatusCode.Created, (await RaiseAsync(client, Body(tx.Id))).StatusCode);
    }

    [Fact]
    public async Task List_filters_by_status()
    {
        var c = await _api.CreateCustomerAsync();
        var tx1 = await _api.CreateTransactionAsync(c.Id);
        var tx2 = await _api.CreateTransactionAsync(c.Id);
        var client = await _api.CustomerClientAsync(c);
        var d1 = await (await RaiseAsync(client, Body(tx1.Id))).ReadAsync<DisputeDetailResponse>();
        await RaiseAsync(client, Body(tx2.Id));
        await client.PostAsync($"/api/disputes/{d1.Id}/withdraw", null);

        var all = await (await client.GetAsync("/api/disputes")).ReadAsync<PagedResponse<DisputeSummaryResponse>>();
        var open = await (await client.GetAsync("/api/disputes?status=Submitted")).ReadAsync<PagedResponse<DisputeSummaryResponse>>();

        Assert.Equal(2, all.TotalCount);
        Assert.Equal(tx2.Id, Assert.Single(open.Items).Transaction.Id);
    }

    [Fact]
    public async Task Dispute_reasons_are_served_with_labels()
    {
        var c = await _api.CreateCustomerAsync();
        var client = await _api.CustomerClientAsync(c);

        var reasons = await (await client.GetAsync("/api/dispute-reasons")).ReadAsync<List<DisputeReasonInfo>>();

        Assert.Equal(Enum.GetValues<DisputeReason>().Length, reasons.Count);
        Assert.All(reasons, r => Assert.False(string.IsNullOrWhiteSpace(r.Label)));
    }
}
