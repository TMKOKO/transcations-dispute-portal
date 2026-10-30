using DisputePortal.Api.Data;
using DisputePortal.Api.DTOs;
using DisputePortal.Api.Models;
using DisputePortal.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace DisputePortal.Api.ViewModels;

/// <summary>The demo agent's reads for <c>AgentController</c>; sending a decision is <see cref="AgentDecisionService"/>.</summary>
public sealed class AgentViewModel
{
    private readonly ReadStore _reads;
    private readonly AgentDecisionService _decisions;
    private readonly ILogger<AgentViewModel> _logger;

    public AgentViewModel(ReadStore reads, AgentDecisionService decisions, ILogger<AgentViewModel> logger)
    {
        _reads = reads;
        _decisions = decisions;
        _logger = logger;
    }

    public async Task<PagedResponse<AgentDisputeResponse>> ListAsync(Guid agentId, DisputeListQuery q, CancellationToken ct)
    {
        var (total, items) = await _reads.QueryAcrossCustomersAsync("agent disputes list", async db =>
        {
            var query = db.Disputes.AsNoTracking();
            if (q.Status is { } status) query = query.Where(d => d.Status == status);

            var total = await query.CountAsync(ct);
            // A projection, so the description never even leaves the database.
            var items = await query
                .OrderByDescending(d => d.CreatedAt)
                .ThenBy(d => d.Id)
                .Skip((q.Page - 1) * q.PageSize)
                .Take(q.PageSize)
                .Select(d => new AgentDisputeResponse(d.Id, d.DisputeNumber, d.Status, d.Reason, d.DisputedAmount,
                    d.Transaction!.Currency, d.Transaction.MerchantName, d.CreatedAt, d.UpdatedAt))
                .ToListAsync(ct);

            return (total, items);
        });

        _logger.LogInformation(
            "Agent disputes query: loaded {Count} of {Total} for agent {AgentId} (status {Status}, page {Page}, size {PageSize})",
            items.Count, total, agentId, q.Status, q.Page, q.PageSize);

        return new PagedResponse<AgentDisputeResponse>(items, q.Page, q.PageSize, total);
    }

    public Task<ServiceResult<AgentDecisionResponse>> SendDecisionAsync(Guid agentId, AgentDecisionRequest request, CancellationToken ct) =>
        _decisions.SendAsync(agentId, request.DisputeId!.Value, request.Decision!.Value, request.Note, ct);
}
