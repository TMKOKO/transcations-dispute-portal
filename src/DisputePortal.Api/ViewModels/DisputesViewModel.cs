using DisputePortal.Api.Data;
using DisputePortal.Api.DTOs;
using DisputePortal.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace DisputePortal.Api.ViewModels;

/// <summary>A raised dispute, and whether it was a replay of an earlier request with the same key.</summary>
public sealed record RaisedDispute(DisputeDetailResponse Dispute, bool Replayed);

/// <summary>
/// The caller's disputes, for <c>DisputesController</c>. Reads are queried here; writes go through
/// <see cref="IDisputeService"/>, which owns every dispute state change.
/// </summary>
public sealed class DisputesViewModel
{
    private readonly AppDbContext _db;
    private readonly IDisputeService _disputes;
    private readonly ILogger<DisputesViewModel> _logger;

    public DisputesViewModel(AppDbContext db, IDisputeService disputes, ILogger<DisputesViewModel> logger)
    {
        _db = db;
        _disputes = disputes;
        _logger = logger;
    }

    public async Task<ServiceResult<RaisedDispute>> RaiseAsync(
        Guid customerId, string idempotencyKey, RaiseDisputeRequest request, CancellationToken ct)
    {
        var result = await _disputes.RaiseAsync(customerId, idempotencyKey, request, ct);
        if (!result.IsSuccess) return ServiceResult<RaisedDispute>.Fail(result.Error!.Kind, result.Error.Code, result.Error.Message);

        var (dispute, replayed) = result.Value!;
        return ServiceResult<RaisedDispute>.Ok(new RaisedDispute(DisputeDetailResponse.From(dispute), replayed));
    }

    public async Task<PagedResponse<DisputeSummaryResponse>> ListAsync(Guid customerId, DisputeListQuery q, CancellationToken ct)
    {
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

        _logger.LogInformation(
            "Disputes query: loaded {Count} of {Total} for customer {CustomerId} (status {Status}, page {Page}, size {PageSize})",
            items.Count, total, customerId, q.Status, q.Page, q.PageSize);

        return new PagedResponse<DisputeSummaryResponse>(
            items.Select(DisputeSummaryResponse.From).ToList(), q.Page, q.PageSize, total);
    }

    public async Task<ServiceResult<DisputeDetailResponse>> GetAsync(Guid customerId, Guid disputeId, CancellationToken ct)
    {
        // Scoped to customerId: another customer's dispute is a 404, never a 403.
        var dispute = await _db.Disputes.AsNoTracking()
            .Include(d => d.Transaction)
            .Include(d => d.History)
            .FirstOrDefaultAsync(d => d.Id == disputeId && d.CustomerId == customerId, ct);

        if (dispute is null)
        {
            _logger.LogInformation("Dispute lookup: {DisputeId} not found for customer {CustomerId}", disputeId, customerId);
            return ServiceResult<DisputeDetailResponse>.Fail(ServiceErrorKind.NotFound, "dispute_not_found", "Dispute not found.");
        }

        _logger.LogInformation("Dispute lookup: loaded {DisputeNumber} ({DisputeId}), status {Status}, for customer {CustomerId}",
            dispute.DisputeNumber, dispute.Id, dispute.Status, customerId);
        return ServiceResult<DisputeDetailResponse>.Ok(DisputeDetailResponse.From(dispute));
    }

    public async Task<ServiceResult<DisputeDetailResponse>> WithdrawAsync(
        Guid customerId, Guid disputeId, string? note, CancellationToken ct)
    {
        var result = await _disputes.WithdrawAsync(customerId, disputeId, note, ct);
        return result.IsSuccess
            ? ServiceResult<DisputeDetailResponse>.Ok(DisputeDetailResponse.From(result.Value!))
            : ServiceResult<DisputeDetailResponse>.Fail(result.Error!.Kind, result.Error.Code, result.Error.Message);
    }

    public IReadOnlyList<DisputeReasonInfo> Reasons() => DisputeReasons.All;
}
