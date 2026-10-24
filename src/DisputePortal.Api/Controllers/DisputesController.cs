using System.Text.RegularExpressions;
using DisputePortal.Api.Data;
using DisputePortal.Api.DTOs;
using DisputePortal.Api.Helpers;
using DisputePortal.Api.Models;
using DisputePortal.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;

namespace DisputePortal.Api.Controllers;

[ApiController]
[Route("api/disputes")]
[Authorize(Roles = JwtTokenService.CustomerRole)]
public partial class DisputesController : ControllerBase
{
    public const string IdempotencyHeader = "Idempotency-Key";
    public const string ReplayedHeader    = "Idempotent-Replayed";

    private readonly AppDbContext _db;
    private readonly IDisputeService _disputes;

    public DisputesController(AppDbContext db, IDisputeService disputes)
    {
        _db = db;
        _disputes = disputes;
    }

    /// <summary>Raise a dispute on one of the caller's transactions.</summary>
    /// <remarks>
    /// Requires an <c>Idempotency-Key</c> header (8–100 chars of <c>[A-Za-z0-9_-]</c>), generated
    /// once per form. Re-sending the same key returns the original dispute with <c>200</c> and
    /// <c>Idempotent-Replayed: true</c> instead of creating another.
    /// </remarks>
    [HttpPost]
    [ProducesResponseType(typeof(DisputeDetailResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(DisputeDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Raise(
        [FromBody] RaiseDisputeRequest request,
        [FromHeader(Name = IdempotencyHeader)] string? idempotencyKey,
        CancellationToken ct)
    {
        var customerId = User.GetCustomerId();
        if (customerId is null) return Unauthorized();

        if (string.IsNullOrEmpty(idempotencyKey) || !IdempotencyKeyFormat().IsMatch(idempotencyKey))
            return this.ProblemWithCode(
                $"An {IdempotencyHeader} header of 8-100 letters, digits, '-' or '_' is required.",
                StatusCodes.Status400BadRequest, "idempotency_key_required");

        var result = await _disputes.RaiseAsync(customerId.Value, idempotencyKey, request, ct);
        if (!result.IsSuccess) return this.ToProblem(result.Error!);

        var (dispute, replayed) = result.Value!;
        var body = DisputeDetailResponse.From(dispute);

        if (replayed)
        {
            Response.Headers[ReplayedHeader] = "true";
            return Ok(body);
        }

        return CreatedAtAction(nameof(Get), new { id = dispute.Id }, body);
    }

    /// <summary>The caller's disputes, newest first.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResponse<DisputeSummaryResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] DisputeListQuery q, CancellationToken ct)
    {
        var customerId = User.GetCustomerId();
        if (customerId is null) return Unauthorized();

        var query = _db.Disputes.AsNoTracking().Where(d => d.CustomerId == customerId);
        if (q.Status is { } status) query = query.Where(d => d.Status == status);

        var total = await query.CountAsync(ct);
        var items = await query
            .Include(d => d.Transaction)
            .OrderByDescending(d => d.CreatedAt)
            .ThenBy(d => d.Id)
            .Skip((q.Page - 1) * q.PageSize)
            .Take(q.PageSize)
            .ToListAsync(ct);

        return Ok(new PagedResponse<DisputeSummaryResponse>(
            items.Select(DisputeSummaryResponse.From).ToList(), q.Page, q.PageSize, total));
    }

    /// <summary>One of the caller's disputes, with its status timeline.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(DisputeDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var customerId = User.GetCustomerId();
        if (customerId is null) return Unauthorized();

        // Scoped to customerId: another customer's dispute is a 404, never a 403.
        var dispute = await _db.Disputes.AsNoTracking()
            .Include(d => d.Transaction)
            .Include(d => d.History)
            .FirstOrDefaultAsync(d => d.Id == id && d.CustomerId == customerId, ct);

        if (dispute is null)
            return this.ProblemWithCode("Dispute not found.", StatusCodes.Status404NotFound, "dispute_not_found");

        return Ok(DisputeDetailResponse.From(dispute));
    }

    /// <summary>Withdraw an open dispute. Allowed while it is Submitted or UnderReview.</summary>
    [HttpPost("{id:guid}/withdraw")]
    [ProducesResponseType(typeof(DisputeDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Withdraw(
        Guid id,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] WithdrawDisputeRequest? request,
        CancellationToken ct)
    {
        var customerId = User.GetCustomerId();
        if (customerId is null) return Unauthorized();

        var result = await _disputes.WithdrawAsync(customerId.Value, id, request?.Note, ct);
        return result.IsSuccess
            ? Ok(DisputeDetailResponse.From(result.Value!))
            : this.ToProblem(result.Error!);
    }

    /// <summary>The reasons a customer can choose from, with display wording.</summary>
    [HttpGet("/api/dispute-reasons")]
    [ProducesResponseType(typeof(IReadOnlyList<DisputeReasonInfo>), StatusCodes.Status200OK)]
    public IActionResult Reasons() => Ok(DisputeReasons.All);

    [GeneratedRegex("^[A-Za-z0-9_-]{8,100}$")]
    private static partial Regex IdempotencyKeyFormat();
}
