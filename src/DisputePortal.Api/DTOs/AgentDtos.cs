using System.ComponentModel.DataAnnotations;
using DisputePortal.Api.Events;
using DisputePortal.Api.Helpers;
using DisputePortal.Api.Models;

namespace DisputePortal.Api.DTOs;

/// <summary>A dispute on the agent's list. No description, withdraw note or customer details: deciding in a demo needs none of them.</summary>
public record AgentDisputeResponse(
    Guid Id,
    string DisputeNumber,
    DisputeStatus Status,
    DisputeReason Reason,
    decimal DisputedAmount,
    string Currency,
    string MerchantName,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public class AgentDecisionRequest
{
    // Mirrored in web/src/lib/agentRules.ts; change both together.
    public const int NoteMaxLength = DecisionHandler.NoteMaxLength;

    [Required]
    public Guid? DisputeId { get; set; }

    [Required]
    [EnumDataType(typeof(DisputeDecision))]
    public DisputeDecision? Decision { get; set; }

    /// <summary>Optional, and shown to the customer on the timeline. Blank or whitespace counts as no note.</summary>
    [PlainText(AllowLineBreaks = true)]
    [StringLength(NoteMaxLength, ErrorMessage = "Note must be 500 characters or fewer.")]
    public string? Note { get; set; }
}

/// <summary>The decision is on its way, not applied: the consumer decides what happens to it.</summary>
public record AgentDecisionResponse(Guid EventId, string EventType, string Topic);
