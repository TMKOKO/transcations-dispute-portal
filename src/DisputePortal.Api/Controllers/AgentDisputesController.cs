using DisputePortal.Api.Data;
using DisputePortal.Api.DTOs;
using DisputePortal.Api.Helpers;
using DisputePortal.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DisputePortal.Api.Controllers;

/// <summary>
/// Back-office review. There is no agent UI in this build — these endpoints exist so the
/// customer-facing lifecycle and timeline can be exercised end to end (see the Postman collection).
/// </summary>
[ApiController]
[Route("api/agent/disputes")]
[Authorize(Roles = JwtTokenService.AgentRole)]
public class AgentDisputesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IDisputeService _disputes;
    private readonly ILogger<AgentDisputesController> _logger;

    public AgentDisputesController(AppDbContext db, IDisputeService disputes, ILogger<AgentDisputesController> logger)
    {
        _db = db;
        _disputes = disputes;
        _logger = logger;
    }

    /// <summary>All customers' disputes, oldest first (a work queue).</summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResponse<DisputeSummaryResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] DisputeListQuery q, CancellationToken ct)
    {
        var query = _db.Disputes.AsNoTracking();
        if (q.Status is { } status) query = query.Where(d => d.Status == status);

        var total = await query.CountAsync(ct);
        var items = await query
            .Include(d => d.Transaction)
            .OrderBy(d => d.CreatedAt)
            .ThenBy(d => d.Id)
            .Skip((q.Page - 1) * q.PageSize)
            .Take(q.PageSize)
            .ToListAsync(ct);

        return Ok(new PagedResponse<DisputeSummaryResponse>(
            items.Select(DisputeSummaryResponse.From).ToList(), q.Page, q.PageSize, total));
    }

    /// <summary>Move a dispute through review. A note is required for Resolved and Rejected.</summary>
    [HttpPatch("{id:guid}/status")]
    [ProducesResponseType(typeof(DisputeDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> UpdateStatus(Guid id, [FromBody] AgentStatusUpdateRequest request, CancellationToken ct)
    {
        var result = await _disputes.AgentTransitionAsync(id, request.Status!.Value, request.Note, ct);
        if (!result.IsSuccess) return this.ToProblem(result.Error!);

        // The history row records the role; the log records which agent, for audit.
        _logger.LogInformation("Agent {AgentId} set dispute {DisputeId} to {Status}",
            User.GetAgentId(), id, request.Status);

        return Ok(DisputeDetailResponse.From(result.Value!));
    }
}
