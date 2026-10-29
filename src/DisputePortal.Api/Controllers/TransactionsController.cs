using DisputePortal.Api.Auth;
using DisputePortal.Api.Data;
using DisputePortal.Api.DTOs;
using DisputePortal.Api.Helpers;
using DisputePortal.Api.Models;
using DisputePortal.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DisputePortal.Api.Controllers;

[ApiController]
[Route("api/transactions")]
[Authorize(Roles = SessionClaims.CustomerRole)]
public class TransactionsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly DisputeEligibility _eligibility;
    private readonly ILogger<TransactionsController> _logger;

    public TransactionsController(AppDbContext db, DisputeEligibility eligibility, ILogger<TransactionsController> logger)
    {
        _db = db;
        _eligibility = eligibility;
        _logger = logger;
    }

    /// <summary>The caller's transactions, newest first, each flagged with whether it can be disputed.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResponse<TransactionResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> List([FromQuery] TransactionQuery q, CancellationToken ct)
    {
        var customerId = User.GetCustomerId();
        if (customerId is null) return Unauthorized();

        var query = _db.Transactions.AsNoTracking().Where(t => t.CustomerId == customerId);

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

        // HasSearch, not the search text: it is free input and may hold anything the customer typed.
        _logger.LogInformation(
            "Transactions query: loaded {Count} of {Total} for customer {CustomerId} (page {Page}, size {PageSize}, from {From}, to {To}, search {HasSearch})",
            rows.Count, total, customerId, q.Page, q.PageSize, q.From, q.To, !string.IsNullOrWhiteSpace(q.Search));

        var items = rows.Select(ToResponse).ToList();
        return Ok(new PagedResponse<TransactionResponse>(items, q.Page, q.PageSize, total));
    }

    /// <summary>One of the caller's transactions, by the id in the request body.</summary>
    /// <remarks>
    /// POST rather than <c>GET /api/transactions/{id}</c>: the id is kept out of the URL, so it does
    /// not land in access logs, proxy logs or browser history. The call is still read-only.
    /// </remarks>
    [HttpPost("lookup")]
    [ProducesResponseType(typeof(TransactionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get([FromBody] TransactionLookupRequest request, CancellationToken ct)
    {
        var customerId = User.GetCustomerId();
        if (customerId is null) return Unauthorized();

        var id = request.TransactionId!.Value;

        // Scoped to customerId, so another customer's transaction is indistinguishable from one
        // that does not exist. Returning 403 here instead would confirm the id was real.
        var row = await _db.Transactions.AsNoTracking()
            .Where(t => t.Id == id && t.CustomerId == customerId)
            .Select(WithLatestDispute)
            .FirstOrDefaultAsync(ct);

        if (row is null)
        {
            _logger.LogInformation("Transaction lookup: {TransactionId} not found for customer {CustomerId}", id, customerId);
            return this.ProblemWithCode("Transaction not found.", StatusCodes.Status404NotFound, "transaction_not_found");
        }

        _logger.LogInformation("Transaction lookup: loaded {TransactionId} for customer {CustomerId}", id, customerId);
        return Ok(ToResponse(row));
    }

    private sealed record Row(Transaction Transaction, DisputeRef? LatestDispute);

    // One query: the latest dispute is a correlated subquery, not a second round trip per row.
    private static readonly System.Linq.Expressions.Expression<Func<Transaction, Row>> WithLatestDispute =
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
