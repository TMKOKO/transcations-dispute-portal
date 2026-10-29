using System.Linq.Expressions;
using DisputePortal.Api.Data;
using DisputePortal.Api.DTOs;
using DisputePortal.Api.Helpers;
using DisputePortal.Api.Models;
using DisputePortal.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace DisputePortal.Api.ViewModels;

/// <summary>The caller's transactions, each flagged with whether it can be disputed, for <c>TransactionsController</c>.</summary>
public sealed class TransactionsViewModel
{
    private readonly ReadStore _reads;
    private readonly DisputeEligibility _eligibility;
    private readonly ILogger<TransactionsViewModel> _logger;

    public TransactionsViewModel(ReadStore reads, DisputeEligibility eligibility, ILogger<TransactionsViewModel> logger)
    {
        _reads = reads;
        _eligibility = eligibility;
        _logger = logger;
    }

    public async Task<PagedResponse<TransactionResponse>> ListAsync(Guid customerId, TransactionQuery q, CancellationToken ct)
    {
        var (total, rows) = await _reads.QueryAsync(customerId, "transactions list", async db =>
        {
            var query = db.Transactions.AsNoTracking().Where(t => t.CustomerId == customerId);

            if (q.From is { } from)
            {
                var fromUtc = Sast.StartOfDayUtc(from);
                query = query.Where(t => t.TransactionDate >= fromUtc);
            }

            if (q.To is { } to)
            {
                // Inclusive of the whole "to" day in SAST.
                var toUtc = Sast.StartOfDayUtc(to.AddDays(1));
                query = query.Where(t => t.TransactionDate < toUtc);
            }

            if (!string.IsNullOrWhiteSpace(q.Search))
            {
                // Escape LIKE wildcards so a search for "100%" means the literal text.
                var pattern = "%" + EscapeLike(q.Search.Trim()) + "%";
                query = query.Where(t => EF.Functions.ILike(t.MerchantName, pattern, "\\")
                                      || EF.Functions.ILike(t.Reference, pattern, "\\"));
            }

            var total = await query.CountAsync(ct);

            var rows = await query
                .OrderByDescending(t => t.TransactionDate)
                .ThenBy(t => t.Id)
                .Skip((q.Page - 1) * q.PageSize)
                .Take(q.PageSize)
                .Select(WithLatestDispute)
                .ToListAsync(ct);

            return (total, rows);
        });

        // HasSearch, not the search text: it is free input and may hold anything the customer typed.
        _logger.LogInformation(
            "Transactions query: loaded {Count} of {Total} for customer {CustomerId} (page {Page}, size {PageSize}, from {From}, to {To}, search {HasSearch})",
            rows.Count, total, customerId, q.Page, q.PageSize, q.From, q.To, !string.IsNullOrWhiteSpace(q.Search));

        var items = rows.Select(ToResponse).ToList();
        return new PagedResponse<TransactionResponse>(items, q.Page, q.PageSize, total);
    }

    public async Task<ServiceResult<TransactionResponse>> GetAsync(Guid customerId, Guid transactionId, CancellationToken ct)
    {
        // Scoped to customerId, so another customer's transaction is indistinguishable from one
        // that does not exist. Returning 403 here instead would confirm the id was real.
        var row = await _reads.QueryAsync(customerId, "transaction lookup", db => db.Transactions.AsNoTracking()
            .Where(t => t.Id == transactionId && t.CustomerId == customerId)
            .Select(WithLatestDispute)
            .FirstOrDefaultAsync(ct));

        if (row is null)
        {
            _logger.LogInformation("Transaction lookup: {TransactionId} not found for customer {CustomerId}", transactionId, customerId);
            return ServiceResult<TransactionResponse>.Fail(ServiceErrorKind.NotFound, "transaction_not_found", "Transaction not found.");
        }

        _logger.LogInformation("Transaction lookup: loaded {TransactionId} for customer {CustomerId}", transactionId, customerId);
        return ServiceResult<TransactionResponse>.Ok(ToResponse(row));
    }

    private sealed record Row(Transaction Transaction, DisputeRef? LatestDispute);

    // One query: the latest dispute is a correlated subquery, not a second round trip per row.
    private static readonly Expression<Func<Transaction, Row>> WithLatestDispute =
        t => new Row(t, t.Disputes
            .OrderByDescending(d => d.CreatedAt)
            .Select(d => new DisputeRef(d.Id, d.DisputeNumber, d.Status))
            .FirstOrDefault());

    private TransactionResponse ToResponse(Row row) =>
        TransactionResponse.From(row.Transaction,
            _eligibility.Evaluate(row.Transaction, row.LatestDispute?.Status),
            row.LatestDispute);

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
