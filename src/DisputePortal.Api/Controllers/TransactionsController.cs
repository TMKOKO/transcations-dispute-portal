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
[Authorize(Roles = JwtTokenService.CustomerRole)]
public class TransactionsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly DisputeEligibility _eligibility;

    public TransactionsController(AppDbContext db, DisputeEligibility eligibility)
    {
        _db = db;
        _eligibility = eligibility;
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

        var items = rows.Select(ToResponse).ToList();
        return Ok(new PagedResponse<TransactionResponse>(items, q.Page, q.PageSize, total));
    }

    /// <summary>One of the caller's transactions.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(TransactionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var customerId = User.GetCustomerId();
        if (customerId is null) return Unauthorized();

        // Scoped to customerId, so another customer's transaction is indistinguishable from one
        // that does not exist. Returning 403 here instead would confirm the id was real.
        var row = await _db.Transactions.AsNoTracking()
            .Where(t => t.Id == id && t.CustomerId == customerId)
            .Select(WithLatestDispute)
            .FirstOrDefaultAsync(ct);

        if (row is null)
            return this.ProblemWithCode("Transaction not found.", StatusCodes.Status404NotFound, "transaction_not_found");

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
