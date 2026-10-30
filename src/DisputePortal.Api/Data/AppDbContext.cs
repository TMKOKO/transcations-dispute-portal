using DisputePortal.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace DisputePortal.Api.Data;

public class AppDbContext : DbContext
{
    public const string DisputeNumberSequence = "dispute_number_seq";

    // Named so DisputeService can tell "this transaction already has an active dispute" apart
    // from any other unique violation when two submits race past the up-front check.
    public const string ActiveDisputeIndex = "ux_disputes_one_active_per_transaction";
    public const string IdempotencyIndex   = "ux_disputes_customer_idempotency_key";

    public const string InboxPrimaryKey = "PK_InboxMessages";

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    // For ReadDbContext: the same model on another connection.
    protected AppDbContext(DbContextOptions options) : base(options) { }

    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<Dispute> Disputes => Set<Dispute>();
    public DbSet<DisputeStatusHistory> DisputeStatusHistory => Set<DisputeStatusHistory>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    // A compile-time constant, not an interpolated string: no caller input ever reaches this SQL.
    private const string NextDisputeSequenceSql =
        "SELECT nextval('" + DisputeNumberSequence + "') AS \"Value\"";

    /// <summary>Next value of the dispute-number sequence. Never reused, even if the insert later fails.</summary>
    public Task<long> NextDisputeSequenceAsync(CancellationToken ct = default) =>
        Database.SqlQueryRaw<long>(NextDisputeSequenceSql).SingleAsync(ct);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Backs DSP-yyyy-NNNNNN. A sequence rather than MAX()+1 so concurrent submits never
        // compute the same number.
        modelBuilder.HasSequence<long>(DisputeNumberSequence).StartsAt(1000).IncrementsBy(1);

        modelBuilder.Entity<Customer>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Email).IsUnique();
            entity.Property(e => e.FirstName).IsRequired().HasMaxLength(100);
            entity.Property(e => e.LastName).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Email).IsRequired().HasMaxLength(200);
            entity.Property(e => e.PasswordHash).IsRequired();
        });

        modelBuilder.Entity<Transaction>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Reference).IsUnique();
            // The transactions page always filters by customer and sorts newest first.
            entity.HasIndex(e => new { e.CustomerId, e.TransactionDate });
            entity.Property(e => e.Reference).IsRequired().HasMaxLength(40);
            entity.Property(e => e.MerchantName).IsRequired().HasMaxLength(200);
            entity.Property(e => e.MerchantCategory).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Amount).HasColumnType("numeric(12,2)");
            entity.Property(e => e.Currency).IsRequired().HasMaxLength(3);
            entity.Property(e => e.Type).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.Channel).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.CardLast4).HasMaxLength(4);
            entity.HasOne(e => e.Customer)
                  .WithMany(c => c.Transactions)
                  .HasForeignKey(e => e.CustomerId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Dispute>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.DisputeNumber).IsUnique();
            entity.HasIndex(e => new { e.CustomerId, e.CreatedAt });

            // At most one open dispute per transaction, enforced by the database. The service checks
            // first for a friendly 409, but two submits in flight can both pass that check; this
            // index makes the second INSERT fail rather than create a duplicate case.
            entity.HasIndex(e => e.TransactionId)
                  .HasDatabaseName(ActiveDisputeIndex)
                  .IsUnique()
                  .HasFilter("\"Status\" IN ('Submitted', 'UnderReview')");

            // Replays of the same submit (retry, double-click) resolve to the original row.
            entity.HasIndex(e => new { e.CustomerId, e.IdempotencyKey })
                  .HasDatabaseName(IdempotencyIndex)
                  .IsUnique();

            entity.Property(e => e.DisputeNumber).IsRequired().HasMaxLength(20);
            entity.Property(e => e.Reason).HasConversion<string>().HasMaxLength(40);
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.Description).IsRequired().HasMaxLength(1000);
            entity.Property(e => e.DisputedAmount).HasColumnType("numeric(12,2)");
            entity.Property(e => e.IdempotencyKey).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Version).IsRowVersion();

            entity.HasOne(e => e.Customer)
                  .WithMany(c => c.Disputes)
                  .HasForeignKey(e => e.CustomerId)
                  .OnDelete(DeleteBehavior.Cascade);
            // Restrict: a transaction with a dispute on it is evidence and must not vanish underneath it.
            entity.HasOne(e => e.Transaction)
                  .WithMany(t => t.Disputes)
                  .HasForeignKey(e => e.TransactionId)
                  .OnDelete(DeleteBehavior.Restrict);
            entity.HasMany(e => e.History)
                  .WithOne(h => h.Dispute)
                  .HasForeignKey(h => h.DisputeId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DisputeStatusHistory>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.DisputeId, e.ChangedAt });
            entity.Property(e => e.FromStatus).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.ToStatus).HasConversion<string>().HasMaxLength(20);
            entity.Property(e => e.Note).HasMaxLength(500);
            entity.Property(e => e.ChangedBy).IsRequired().HasMaxLength(20);
        });

        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.EventId).IsUnique();
            // OutboxRelay only ever reads unpublished rows, oldest first.
            entity.HasIndex(e => e.Id)
                  .HasDatabaseName("ix_outbox_unpublished")
                  .HasFilter("\"PublishedAt\" IS NULL");
            entity.Property(e => e.EventType).IsRequired().HasMaxLength(40);
            entity.Property(e => e.Topic).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Key).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Payload).IsRequired().HasColumnType("jsonb");
            entity.Property(e => e.LastError).HasMaxLength(500);
        });

        modelBuilder.Entity<InboxMessage>(entity =>
        {
            // Named so DecisionHandler can tell a duplicate delivery from any other unique violation.
            entity.HasKey(e => e.EventId).HasName(InboxPrimaryKey);
            entity.Property(e => e.EventType).IsRequired().HasMaxLength(40);
            entity.Property(e => e.Outcome).IsRequired().HasMaxLength(20);
        });
    }
}
