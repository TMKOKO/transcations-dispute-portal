using DisputePortal.Api.Auth;
using DisputePortal.Api.DTOs;
using DisputePortal.Api.Helpers;
using DisputePortal.Api.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DisputePortal.Api.Controllers;

/// <summary>
/// The demo agent: plays the bank's back office from the portal. Off unless <c>Agent:Enabled</c>.
/// </summary>
[ApiController]
[Route("api/agent")]
[Authorize(Roles = SessionClaims.AgentRole)]
public class AgentController : ControllerBase
{
    private readonly AgentViewModel _agent;

    public AgentController(AgentViewModel agent)
    {
        _agent = agent;
    }

    /// <summary>Does nothing but restart the session's idle timer, as "Stay signed in" needs.</summary>
    [HttpGet("session")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public IActionResult Session() => NoContent();

    /// <summary>Every customer's disputes, newest first.</summary>
    [HttpGet("disputes")]
    [ProducesResponseType(typeof(PagedResponse<AgentDisputeResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] DisputeListQuery q, CancellationToken ct)
    {
        var agentId = User.GetAgentId();
        if (agentId is null) return Unauthorized();

        return Ok(await _agent.ListAsync(agentId.Value, q, ct));
    }

    /// <summary>Send a decision on a dispute, by the id in the request body.</summary>
    /// <remarks>
    /// 202, because nothing has changed yet: the decision goes to its Kafka topic and the consumer
    /// applies it, skips it as stale or sends it to the dead-letter topic.
    /// </remarks>
    [HttpPost("disputes/decision")]
    [ProducesResponseType(typeof(AgentDecisionResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Decide([FromBody] AgentDecisionRequest request, CancellationToken ct)
    {
        var agentId = User.GetAgentId();
        if (agentId is null) return Unauthorized();

        var result = await _agent.SendDecisionAsync(agentId.Value, request, ct);
        if (!result.IsSuccess) return this.ToProblem(result.Error!);

        // No Location header: there is deliberately no GET URL that carries a dispute id.
        return StatusCode(StatusCodes.Status202Accepted, result.Value);
    }
}
