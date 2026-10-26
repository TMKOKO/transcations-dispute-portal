namespace DisputePortal.Api.Models;

/// <summary>
/// A posted account transaction. Read-only from the portal's point of view: in a real bank these
/// rows are synced from core banking, so nothing in the API mutates them.
/// </summary>
public class Transaction
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid CustomerId { get; set; }
    public Customer? Customer { get; set; }

    public string Reference { get; set; } = string.Empty;       // e.g. TXN-20260914-7F3A2C
    public string MerchantName { get; set; } = string.Empty;
    public string MerchantCategory { get; set; } = string.Empty;

    // Always positive; Type carries the direction.
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "ZAR";

    public TransactionType Type { get; set; }
    public TransactionChannel Channel { get; set; }
    public TransactionStatus Status { get; set; }
    public string? CardLast4 { get; set; }

    public DateTime TransactionDate { get; set; }

    public ICollection<Dispute> Disputes { get; set; } = [];
}

public enum TransactionType
{
    Debit,
    Credit
}

public enum TransactionChannel
{
    Card,
    Eft,
    DebitOrder
}

public enum TransactionStatus
{
    Pending,
    Settled,
    Reversed
}
