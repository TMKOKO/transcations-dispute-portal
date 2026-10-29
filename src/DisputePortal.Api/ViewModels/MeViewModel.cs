using DisputePortal.Api.Data;
using DisputePortal.Api.DTOs;
using Microsoft.EntityFrameworkCore;

namespace DisputePortal.Api.ViewModels;

/// <summary>The signed-in customer's profile, for <c>MeController</c>.</summary>
public sealed class MeViewModel
{
    private readonly AppDbContext _db;
    private readonly ILogger<MeViewModel> _logger;

    public MeViewModel(AppDbContext db, ILogger<MeViewModel> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>Null when the session's customer no longer exists.</summary>
    public async Task<MeResponse?> GetAsync(Guid customerId, CancellationToken ct)
    {
        var me = await _db.Customers.AsNoTracking()
            .Where(c => c.Id == customerId)
            .Select(c => new MeResponse(c.Id, c.FirstName, c.LastName, c.Email))
            .FirstOrDefaultAsync(ct);

        if (me is null)
            _logger.LogWarning("Profile lookup: customer {CustomerId} from a live session no longer exists", customerId);
        else
            _logger.LogInformation("Profile lookup: loaded customer {CustomerId}", customerId);

        return me;
    }
}
