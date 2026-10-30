using DisputePortal.Api.Models;
using DisputePortal.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace DisputePortal.Api.Data;

/// <summary>
/// Demo data for local runs. Development only — Program.cs gates it on <c>IsDevelopment()</c>,
/// because every seeded account shares one published password.
/// <para>
/// Deterministic (fixed RNG seed) and anchored to "now", so every eligibility case the UI explains
/// is always present: transactions outside the 120-day window, credits, one reversed, one pending,
/// a duplicate charge to dispute, and existing disputes in every status with their timelines.
/// </para>
/// </summary>
public static class DbSeeder
{
    public const string DemoPassword = "Demo@12345!";

    private sealed record Merchant(string Name, string Category, TransactionChannel Channel, decimal Min, decimal Max);

    private static readonly Merchant[] CardMerchants =
    [
        new("Woolworths Food",  "Groceries",       TransactionChannel.Card, 120,  1500),
        new("Checkers Sixty60", "Groceries",       TransactionChannel.Card,  80,   900),
        new("Pick n Pay",       "Groceries",       TransactionChannel.Card,  50,  1200),
        new("Takealot.com",     "Online shopping", TransactionChannel.Card, 150,  6000),
        new("Uber Eats",        "Restaurants",     TransactionChannel.Card,  90,   450),
        new("Uber Trip",        "Transport",       TransactionChannel.Card,  45,   320),
        new("Engen Garage",     "Fuel",            TransactionChannel.Card, 300,  1100),
        new("Shell Ultra City", "Fuel",            TransactionChannel.Card, 300,  1100),
        new("Dis-Chem",         "Health",          TransactionChannel.Card,  80,   900),
        new("Mr Price",         "Clothing",        TransactionChannel.Card, 150,  1500),
        new("Superbalist",      "Online shopping", TransactionChannel.Card, 300,  2500),
        new("Nando's",          "Restaurants",     TransactionChannel.Card, 120,   600),
    ];

    /// <summary>
    /// The demo agent. Separate from <see cref="SeedAsync"/>, which stops as soon as customers exist:
    /// a database seeded before this login existed must still get its agent.
    /// </summary>
    public static async Task SeedAgentAsync(AppDbContext db, TimeProvider clock, ILogger logger, CancellationToken ct = default)
    {
        if (await db.Agents.AnyAsync(ct))
        {
            logger.LogInformation("Demo agent seed skipped: an agent already exists");
            return;
        }

        var agent = new Agent
        {
            DisplayName  = "Disputes Agent",
            Email        = "agent@example.com",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(DemoPassword),
            CreatedAt    = clock.GetUtcNow().UtcDateTime,
        };
        db.Agents.Add(agent);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Demo agent seed saved agent {AgentId}", agent.Id);
    }

    public static async Task SeedAsync(AppDbContext db, TimeProvider clock, ILogger logger, CancellationToken ct = default)
    {
        if (await db.Customers.AnyAsync(ct))
        {
            logger.LogInformation("Demo seed skipped: customers already exist");
            return;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var hash = BCrypt.Net.BCrypt.HashPassword(DemoPassword);

        (string First, string Last, string Email, string Card)[] people =
        [
            ("Thandi", "Nkosi",   "thandi@example.com", "4821"),
            ("Sipho",  "Dlamini", "sipho@example.com",  "7390"),
            ("Lerato", "Mokoena", "lerato@example.com", "1056"),
        ];

        var references = new HashSet<string>();

        for (var i = 0; i < people.Length; i++)
        {
            var p = people[i];
            var customer = new Customer
            {
                FirstName    = p.First,
                LastName     = p.Last,
                Email        = p.Email,
                PasswordHash = hash,
                CreatedAt    = now.AddYears(-2),
            };
            db.Customers.Add(customer);

            var rng = new Random(42 + i);
            var txns = GenerateTransactions(customer.Id, p.Card, now, rng, references);
            db.Transactions.AddRange(txns);

            // Thandi gets one dispute in every status; the others get a couple each.
            var statuses = i == 0
                ? new[] { DisputeStatus.Submitted, DisputeStatus.UnderReview, DisputeStatus.Resolved, DisputeStatus.Rejected, DisputeStatus.Withdrawn }
                : new[] { DisputeStatus.UnderReview, DisputeStatus.Resolved };

            var candidates = txns
                .Where(t => t.Type == TransactionType.Debit
                         && t.Status == TransactionStatus.Settled
                         && t.Channel == TransactionChannel.Card
                         && t.MerchantName != "Computicket"           // keep the duplicate pair free for the live demo
                         && now - t.TransactionDate < TimeSpan.FromDays(90)
                         && now - t.TransactionDate > TimeSpan.FromDays(12))
                .OrderByDescending(t => t.TransactionDate)
                .Where((_, index) => index % 3 == 0)                  // spread them out
                .Take(statuses.Length)
                .ToList();

            for (var s = 0; s < candidates.Count; s++)
                db.Disputes.Add(await BuildDisputeAsync(db, customer.Id, candidates[s], statuses[s], ct));
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Demo seed saved {Customers} customers, {Transactions} transactions and {Disputes} disputes",
            db.Customers.Local.Count, db.Transactions.Local.Count, db.Disputes.Local.Count);
    }

    private static List<Transaction> GenerateTransactions(
        Guid customerId, string card, DateTime now, Random rng, HashSet<string> references)
    {
        var list = new List<Transaction>();

        Transaction Add(string merchant, string category, TransactionChannel channel, TransactionType type,
            decimal amount, DateTime when, TransactionStatus status = TransactionStatus.Settled)
        {
            var tx = new Transaction
            {
                CustomerId       = customerId,
                Reference        = NewReference(when, rng, references),
                MerchantName     = merchant,
                MerchantCategory = category,
                Amount           = amount,
                Type             = type,
                Channel          = channel,
                Status           = status,
                CardLast4        = channel == TransactionChannel.Card ? card : null,
                TransactionDate  = DateTime.SpecifyKind(when, DateTimeKind.Utc),
            };
            list.Add(tx);
            return tx;
        }

        // Monthly items across six months (so some fall outside the 120-day window).
        for (var m = 0; m < 6; m++)
        {
            var monthAnchor = now.AddDays(-30 * m - 3);
            Add("Salary - Acme Holdings", "Income", TransactionChannel.Eft, TransactionType.Credit,
                28_500m, monthAnchor.Date.AddHours(6));
            Add("Vodacom Debit Order", "Telecoms", TransactionChannel.DebitOrder, TransactionType.Debit,
                599m, monthAnchor.Date.AddDays(-2).AddHours(4));
            Add("Discovery Insure", "Insurance", TransactionChannel.DebitOrder, TransactionType.Debit,
                1_349.50m, monthAnchor.Date.AddDays(-1).AddHours(5));
            Add("Netflix.com", "Subscriptions", TransactionChannel.Card, TransactionType.Debit,
                199m, monthAnchor.Date.AddDays(-5).AddHours(9));
        }

        // Day-to-day card spend over the last 180 days.
        for (var n = 0; n < 50; n++)
        {
            var m = CardMerchants[rng.Next(CardMerchants.Length)];
            var when = now.AddMinutes(-rng.Next(60 * 24 * 2, 60 * 24 * 180));
            var amount = Math.Round(m.Min + (decimal)rng.NextDouble() * (m.Max - m.Min), 2);
            Add(m.Name, m.Category, m.Channel, TransactionType.Debit, amount, when);
        }

        // A duplicate charge: same merchant, same amount, three minutes apart.
        var dupeAt = now.AddDays(-9).Date.AddHours(17);
        Add("Computicket", "Entertainment", TransactionChannel.Card, TransactionType.Debit, 850m, dupeAt);
        Add("Computicket", "Entertainment", TransactionChannel.Card, TransactionType.Debit, 850m, dupeAt.AddMinutes(3));

        // Reversed by the merchant, and its matching refund credit.
        var reversedAt = now.AddDays(-20).Date.AddHours(13);
        Add("Takealot.com", "Online shopping", TransactionChannel.Card, TransactionType.Debit, 2_199m,
            reversedAt, TransactionStatus.Reversed);
        Add("Refund - Takealot.com", "Refunds", TransactionChannel.Card, TransactionType.Credit, 2_199m,
            reversedAt.AddDays(2));

        // Still pending: authorised a few hours ago, not yet settled.
        Add("Engen Garage", "Fuel", TransactionChannel.Card, TransactionType.Debit, 642.18m,
            now.AddHours(-5), TransactionStatus.Pending);

        return list;
    }

    private static async Task<Dispute> BuildDisputeAsync(
        AppDbContext db, Guid customerId, Transaction tx, DisputeStatus target, CancellationToken ct)
    {
        var raisedAt = tx.TransactionDate.AddDays(3);
        var sequence = await db.NextDisputeSequenceAsync(ct);

        var (reason, description) = target switch
        {
            DisputeStatus.Rejected  => (DisputeReason.GoodsNotReceived, "I ordered this online and it was never delivered to my address."),
            DisputeStatus.Withdrawn => (DisputeReason.IncorrectAmount,  "I was charged more than the price shown at the till."),
            DisputeStatus.Resolved  => (DisputeReason.Unauthorised,     "I do not recognise this transaction and did not make it."),
            _                       => (DisputeReason.DuplicateCharge,  "This payment looks like it was taken twice for one purchase."),
        };

        var dispute = Dispute.Raise(
            DisputeNumber.Format(raisedAt.Year, sequence),
            customerId, tx.Id, reason, description, tx.Amount,
            idempotencyKey: $"seed-{Guid.NewGuid():N}",
            raisedAt);

        if (target == DisputeStatus.Withdrawn)
        {
            dispute.TransitionTo(DisputeStatus.Withdrawn, DisputeActor.Customer,
                "The merchant refunded me directly.", raisedAt.AddDays(1));
            return dispute;
        }

        if (target == DisputeStatus.Submitted) return dispute;

        dispute.TransitionTo(DisputeStatus.UnderReview, DisputeActor.Agent,
            "Your case has been assigned to a disputes specialist.", raisedAt.AddDays(1));

        if (target == DisputeStatus.Resolved)
            dispute.TransitionTo(DisputeStatus.Resolved, DisputeActor.Agent,
                $"Confirmed as unauthorised. R{tx.Amount:0.00} has been credited to your account.", raisedAt.AddDays(5));

        if (target == DisputeStatus.Rejected)
            dispute.TransitionTo(DisputeStatus.Rejected, DisputeActor.Agent,
                "The merchant supplied proof of delivery signed at your address.", raisedAt.AddDays(6));

        return dispute;
    }

    private static string NewReference(DateTime when, Random rng, HashSet<string> taken)
    {
        string reference;
        do reference = $"TXN-{when:yyyyMMdd}-{rng.Next(0x100000, 0xFFFFFF):X6}";
        while (!taken.Add(reference));
        return reference;
    }
}
