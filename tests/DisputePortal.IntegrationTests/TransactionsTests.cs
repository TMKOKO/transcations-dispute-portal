using System.Net;
using System.Net.Http.Json;
using DisputePortal.Api.DTOs;
using DisputePortal.Api.Models;
using DisputePortal.Api.Services;
using DisputePortal.IntegrationTests.Infrastructure;

namespace DisputePortal.IntegrationTests;

[Collection(ApiCollection.Name)]
public class TransactionsTests : IAsyncLifetime
{
    private readonly ApiFactory _api;

    public TransactionsTests(PostgresFixture db) => _api = new ApiFactory(db);

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => _api.DisposeAsync().AsTask();

    [Fact]
    public async Task List_returns_only_the_callers_transactions_newest_first()
    {
        var a = await _api.CreateCustomerAsync();
        var b = await _api.CreateCustomerAsync();
        var older = await _api.CreateTransactionAsync(a.Id, daysAgo: 10);
        var newer = await _api.CreateTransactionAsync(a.Id, daysAgo: 1);
        await _api.CreateTransactionAsync(b.Id);

        var client = await _api.CustomerClientAsync(a);
        var page = await (await client.GetAsync("/api/transactions")).ReadAsync<PagedResponse<TransactionResponse>>();

        Assert.Equal(2, page.TotalCount);
        Assert.Equal([newer.Id, older.Id], page.Items.Select(t => t.Id));
    }

    [Fact]
    public async Task Another_customers_transaction_is_404_not_403()
    {
        var a = await _api.CreateCustomerAsync();
        var b = await _api.CreateCustomerAsync();
        var bTx = await _api.CreateTransactionAsync(b.Id);

        var client = await _api.CustomerClientAsync(a);
        var response = await client.PostAsJsonAsync("/api/transactions/lookup", new { transactionId = bTx.Id });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("transaction_not_found", await response.ProblemCodeAsync());
    }

    [Fact]
    public async Task Lookup_takes_the_id_from_the_body()
    {
        var c = await _api.CreateCustomerAsync();
        var tx = await _api.CreateTransactionAsync(c.Id);
        var client = await _api.CustomerClientAsync(c);

        var found = await (await client.PostAsJsonAsync("/api/transactions/lookup", new { transactionId = tx.Id }))
            .ReadAsync<TransactionResponse>();
        Assert.Equal(tx.Id, found.Id);

        // A missing id is a validation error, and the old URL form no longer exists.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/transactions/lookup", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/transactions/{tx.Id}")).StatusCode);
    }

    [Fact]
    public async Task Each_row_carries_its_eligibility()
    {
        var c = await _api.CreateCustomerAsync();
        var debit    = await _api.CreateTransactionAsync(c.Id);
        var credit   = await _api.CreateTransactionAsync(c.Id, type: TransactionType.Credit);
        var reversed = await _api.CreateTransactionAsync(c.Id, status: TransactionStatus.Reversed);
        var pending  = await _api.CreateTransactionAsync(c.Id, status: TransactionStatus.Pending);
        var old      = await _api.CreateTransactionAsync(c.Id, daysAgo: 150);

        var client = await _api.CustomerClientAsync(c);
        var items = (await (await client.GetAsync("/api/transactions")).ReadAsync<PagedResponse<TransactionResponse>>())
            .Items.ToDictionary(t => t.Id);

        Assert.True(items[debit.Id].CanDispute);
        Assert.Null(items[debit.Id].IneligibleMessage);
        Assert.Equal(IneligibleReason.NotADebit, items[credit.Id].IneligibleCode);
        Assert.Equal(IneligibleReason.Reversed, items[reversed.Id].IneligibleCode);
        Assert.Equal(IneligibleReason.Pending, items[pending.Id].IneligibleCode);
        Assert.Equal(IneligibleReason.OutsideWindow, items[old.Id].IneligibleCode);
        Assert.All(items.Values.Where(t => !t.CanDispute), t => Assert.False(string.IsNullOrEmpty(t.IneligibleMessage)));
    }

    [Fact]
    public async Task Search_matches_merchant_case_insensitively_and_treats_wildcards_literally()
    {
        var c = await _api.CreateCustomerAsync();
        await _api.CreateTransactionAsync(c.Id, merchant: "Woolworths Food");
        await _api.CreateTransactionAsync(c.Id, merchant: "Engen Garage");
        await _api.CreateTransactionAsync(c.Id, merchant: "100% Juice Bar");

        var client = await _api.CustomerClientAsync(c);

        async Task<IReadOnlyList<TransactionResponse>> Search(string q) =>
            (await (await client.GetAsync($"/api/transactions?search={Uri.EscapeDataString(q)}"))
                .ReadAsync<PagedResponse<TransactionResponse>>()).Items;

        Assert.Equal("Woolworths Food", Assert.Single(await Search("WOOLW")).MerchantName);
        Assert.Equal("100% Juice Bar", Assert.Single(await Search("0%")).MerchantName);
        Assert.Empty(await Search("_"));
    }

    [Fact]
    public async Task Paging_reports_totals()
    {
        var c = await _api.CreateCustomerAsync();
        for (var i = 0; i < 5; i++) await _api.CreateTransactionAsync(c.Id, daysAgo: i + 1);

        var client = await _api.CustomerClientAsync(c);
        var page2 = await (await client.GetAsync("/api/transactions?page=2&pageSize=2"))
            .ReadAsync<PagedResponse<TransactionResponse>>();

        Assert.Equal(5, page2.TotalCount);
        Assert.Equal(3, page2.TotalPages);
        Assert.Equal(2, page2.Items.Count);
    }

    [Fact]
    public async Task Date_filter_is_inclusive_and_validated()
    {
        var c = await _api.CreateCustomerAsync();
        var inRange = await _api.CreateTransactionAsync(c.Id, daysAgo: 3);
        await _api.CreateTransactionAsync(c.Id, daysAgo: 30);

        var client = await _api.CustomerClientAsync(c);
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(2));
        var from = today.AddDays(-7).ToString("yyyy-MM-dd");
        var to = today.ToString("yyyy-MM-dd");

        var page = await (await client.GetAsync($"/api/transactions?from={from}&to={to}"))
            .ReadAsync<PagedResponse<TransactionResponse>>();
        Assert.Equal(inRange.Id, Assert.Single(page.Items).Id);

        var invalid = await client.GetAsync($"/api/transactions?from={to}&to={from}");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Theory]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=101")]
    [InlineData("page=0")]
    public async Task Out_of_range_paging_is_400(string query)
    {
        var c = await _api.CreateCustomerAsync();
        var client = await _api.CustomerClientAsync(c);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/transactions?{query}")).StatusCode);
    }
}
