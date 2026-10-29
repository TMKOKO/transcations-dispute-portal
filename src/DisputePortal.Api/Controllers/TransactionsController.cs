using DisputePortal.Api.Auth;
using DisputePortal.Api.DTOs;
using DisputePortal.Api.Helpers;
using DisputePortal.Api.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DisputePortal.Api.Controllers;

[ApiController]
[Route("api/transactions")]
[Authorize(Roles = SessionClaims.CustomerRole)]
public class TransactionsController : ControllerBase
{
    private readonly TransactionsViewModel _transactions;

    public TransactionsController(TransactionsViewModel transactions)
    {
        _transactions = transactions;
    }

    /// <summary>The caller's transactions, newest first, each flagged with whether it can be disputed.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResponse<TransactionResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> List([FromQuery] TransactionQuery q, CancellationToken ct)
    {
        var customerId = User.GetCustomerId();
        if (customerId is null) return Unauthorized();

        return Ok(await _transactions.ListAsync(customerId.Value, q, ct));
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

        var result = await _transactions.GetAsync(customerId.Value, request.TransactionId!.Value, ct);
        return result.IsSuccess ? Ok(result.Value) : this.ToProblem(result.Error!);
    }
}
