namespace DisputePortal.Api.Models;

public class Dispute
{
    /// <summary>
    /// The only legal status moves. Anything not listed here — including every move out of a
    /// terminal status — is rejected by <see cref="TransitionTo"/>.
    /// </summary>
    public static readonly IReadOnlyDictionary<DisputeStatus, DisputeStatus[]> AllowedTransitions =
        new Dictionary<DisputeStatus, DisputeStatus[]>
        {
            [DisputeStatus.Submitted]   = [DisputeStatus.UnderReview, DisputeStatus.Rejected, DisputeStatus.Withdrawn],
            [DisputeStatus.UnderReview] = [DisputeStatus.Resolved, DisputeStatus.Rejected, DisputeStatus.Withdrawn],
            [DisputeStatus.Resolved]    = [],
            [DisputeStatus.Rejected]    = [],
            [DisputeStatus.Withdrawn]   = [],
        };

    public Guid Id { get; set; } = Guid.NewGuid();
    public string DisputeNumber { get; set; } = string.Empty;

    public Guid CustomerId { get; set; }
    public Customer? Customer { get; set; }

    public Guid TransactionId { get; set; }
    public Transaction? Transaction { get; set; }

    public DisputeReason Reason { get; set; }
    public string Description { get; set; } = string.Empty;
    public decimal DisputedAmount { get; set; }

    // Private setter: status only moves through TransitionTo, which also writes the history row.
    public DisputeStatus Status { get; private set; } = DisputeStatus.Submitted;

    // Client-supplied, unique per customer. Lets a retried or double-clicked submit return the
    // original dispute instead of creating a second one.
    public string IdempotencyKey { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? ResolvedAt { get; private set; }

    // Mapped to Postgres xmin. A customer withdrawing while an agent resolves the same dispute
    // makes the second SaveChanges fail instead of silently overwriting the first.
    public uint Version { get; set; }

    public List<DisputeStatusHistory> History { get; set; } = [];

    public bool IsActive => IsActiveStatus(Status);

    public static bool IsActiveStatus(DisputeStatus status) =>
        status is DisputeStatus.Submitted or DisputeStatus.UnderReview;

    public static bool IsTerminal(DisputeStatus status) => AllowedTransitions[status].Length == 0;

    /// <summary>Create a new dispute in <see cref="DisputeStatus.Submitted"/> with its first timeline row.</summary>
    public static Dispute Raise(
        string disputeNumber,
        Guid customerId,
        Guid transactionId,
        DisputeReason reason,
        string description,
        decimal disputedAmount,
        string idempotencyKey,
        DateTime nowUtc)
    {
        var dispute = new Dispute
        {
            DisputeNumber  = disputeNumber,
            CustomerId     = customerId,
            TransactionId  = transactionId,
            Reason         = reason,
            Description    = description,
            DisputedAmount = disputedAmount,
            IdempotencyKey = idempotencyKey,
            CreatedAt      = nowUtc,
            UpdatedAt      = nowUtc,
        };

        dispute.History.Add(new DisputeStatusHistory
        {
            DisputeId  = dispute.Id,
            FromStatus = null,
            ToStatus   = DisputeStatus.Submitted,
            ChangedBy  = DisputeActor.Customer,
            ChangedAt  = nowUtc,
        });

        return dispute;
    }

    public bool CanTransitionTo(DisputeStatus to) => AllowedTransitions[Status].Contains(to);

    /// <summary>Move to <paramref name="to"/> and append the matching timeline row.</summary>
    /// <exception cref="InvalidDisputeTransitionException">The move is not in <see cref="AllowedTransitions"/>.</exception>
    public DisputeStatusHistory TransitionTo(DisputeStatus to, string actor, string? note, DateTime nowUtc)
    {
        if (!CanTransitionTo(to))
            throw new InvalidDisputeTransitionException(Status, to);

        var entry = new DisputeStatusHistory
        {
            DisputeId  = Id,
            FromStatus = Status,
            ToStatus   = to,
            Note       = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            ChangedBy  = actor,
            ChangedAt  = nowUtc,
        };

        Status    = to;
        UpdatedAt = nowUtc;
        if (IsTerminal(to)) ResolvedAt = nowUtc;

        History.Add(entry);
        return entry;
    }
}

public sealed class InvalidDisputeTransitionException(DisputeStatus from, DisputeStatus to)
    : InvalidOperationException($"A dispute cannot move from {from} to {to}.")
{
    public DisputeStatus From { get; } = from;
    public DisputeStatus To { get; } = to;
}
