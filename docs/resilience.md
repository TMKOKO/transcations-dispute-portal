# Resilience: dispute events and reads

How the portal keeps working when its dependencies fail, and how dispute status changes travel between the portal and the bank's back office.

## The short version

- **Writes never depend on Kafka.**
  - A dispute and its event are saved in one Postgres transaction (transactional outbox).
  - A background publisher sends the event later.
  - If Kafka is down, customers still raise and withdraw disputes; the events just go out late.
- **Decisions are applied exactly once in effect.**
  - Kafka delivers at least once.
  - The API records every event id it has handled (inbox) in the same transaction as the status change, so a redelivery does nothing.
- **Reads survive a replica outage and a slow replica.**
  - Customer reads go to a streaming replica. If it's unreachable, the same read runs on the primary.
  - For 5 seconds after a customer writes, their reads go to the primary, so they always see their own change.
- **Transient database errors are retried.** EF Core's retrying execution strategy handles them, with 10-second command timeouts, instead of returning 500s.

## Dispute events

```
 Portal ──HTTP──▶ API ──(one DB transaction)──▶ Disputes + OutboxMessages
                                                         │
                                        OutboxPublisherService (every 1 s)
                                                         ▼
                                   Kafka: dispute-submitted, dispute-withdrawn ──▶ Back office
                                                                                       │
 Portal ◀── timeline ── API ◀── DecisionConsumer ◀── Kafka: dispute-under-review ◀─────┤
                         │                                 dispute-refund-issued ◀─────┤
                         │                                 dispute-rejected ◀──────────┘
                         └──▶ dispute-decisions-dlq (decisions that can't be applied)
```

| Topic | Producer → consumer | Event | Effect on the dispute |
|---|---|---|---|
| `dispute-submitted` | API → back office | DisputeSubmitted | (the API created it) |
| `dispute-withdrawn` | API → back office | DisputeWithdrawn | (the customer withdrew; stop reviewing) |
| `dispute-under-review` | back office → API | UnderReview | → Under review |
| `dispute-refund-issued` | back office → API | RefundIssued | → Resolved |
| `dispute-rejected` | back office → API | Rejected | → Rejected |
| `dispute-decisions-dlq` | API → operations | the original message | none; a person looks at it |

**Topics.**
- Every topic has 3 partitions. Every message is **keyed by dispute id**, so one dispute's events land on one partition and are read in order.
- Topics are created by `kafka-init` (`docker/kafka/create-topics.sh`), and auto-create is off on the broker.

**Envelope.**
- A JSON envelope (`Events/DisputeEvent.cs`): `eventId`, `eventType`, `schemaVersion`, `occurredAt`, `disputeId`, `disputeNumber`, `customerId` and `transactionId`.
- On submit it adds `reason`, `amount` and `currency`. A decision can carry a `note`, which the customer sees on the timeline.
- **The description is never on a topic.** Topics are long-lived logs that can't be redacted, and the description is free text that may hold personal information. A consumer that needs it reads the dispute by id (claim check). The customer's withdraw note is left out for the same reason.

### Publishing: transactional outbox

The problem: "save the dispute, then publish to Kafka" has two failure windows.
- If the publish fails after the save, the event is lost.
- If you publish first and the save then fails, the event announces something that never happened.

The outbox removes both windows:
1. `DisputeService` adds an `OutboxMessages` row in the **same `SaveChanges`** as the dispute insert or withdraw. The two commit together or not at all.
2. `OutboxPublisherService` runs `OutboxRelay` every second:
   - it claims up to 50 unpublished rows, oldest first, with `FOR UPDATE SKIP LOCKED` (so several API instances never take the same row);
   - it publishes each one and sets `PublishedAt`.
3. The producer is idempotent (`EnableIdempotence`, `Acks=All`), so its own retries never duplicate or reorder.
4. **On a failed publish:**
   - the row records `Attempts` and `LastError`;
   - the rest of the batch waits, so a dispute's later event can't overtake its earlier one;
   - the publisher backs off exponentially, from 1 second up to 30 seconds.
5. **Delivery is at least once.** A crash after the publish but before `PublishedAt` commits re-sends the row, so consumers dedupe on `eventId`.

### Consuming: idempotent consumer

`DecisionConsumer` reads the three decision topics in consumer group `dispute-portal-api`, with auto-commit off. `DecisionHandler` treats each message as follows:

| Case | What happens | Offset |
|---|---|---|
| `eventId` already in `InboxMessages` | Skipped (duplicate) | Committed |
| Move allowed by the state machine | Status change, timeline row (actor `agent`) and inbox row, in **one** `SaveChanges` | Committed after the save |
| Dispute already final, or already at this status | Recorded as stale, nothing changes. Covers late events and decisions on a withdrawn dispute | Committed |
| Not allowed yet (e.g. RefundIssued while still Submitted) | Sent to `dispute-decisions-dlq` with reason `transition_not_allowed` | Committed |
| Bad JSON, wrong event type, unsupported `schemaVersion`, unknown dispute, invalid note | Sent to the DLQ with a reason header | Committed |
| A concurrent update (the customer withdrew mid-flight) | `xmin` conflict: reload and decide again, up to 3 times | n/a |
| Database down, or any other exception | Seek back to the same offset and back off (1 s up to 30 s). Nothing is skipped | Not committed |

Committing only after the database commit means a crash between the two redelivers the message, and the inbox turns that into a no-op. Anything that would fail the same way every time goes to the DLQ instead of blocking its partition (no poison-pill loop).

**Trade-off: a topic per status.**
- Kafka orders messages within a partition, not across topics. A `RefundIssued` can therefore be read before the `UnderReview` that preceded it.
- The handler copes:
  - late events are stale and skipped;
  - early ones go to the DLQ for a person to look at;
  - nothing is applied out of order.
- A single `dispute-decisions` topic keyed by dispute id would guarantee order and remove that DLQ case. It's the change I'd make if out-of-order decisions became common.

## Reads

| Layer | What | Where |
|---|---|---|
| Retries | `EnableRetryOnFailure(3, 2 s)` on the primary; transient Npgsql errors (failover, dropped connection) are retried | `Program.cs` |
| Timeouts | 10-second command timeout; 2-second connect timeout on the replica | `Program.cs` |
| Replica | `ReadDbContext` on `ConnectionStrings:ReadOnlyConnection`, no tracking, `SaveChanges` throws | `Data/ReadDbContext.cs` |
| Failover | Replica unreachable: the read runs once more on the primary, with a Warning log | `Data/ReadStore.cs` |
| Read-your-writes | After a raise or withdraw, Redis key `rw:{customerId}` (5-second TTL) sends that customer's reads to the primary | `Data/ReadYourWrites.cs` |
| Health | `read-replica` and `outbox` checks report **Degraded**, not Unhealthy: customers are still served | `/health` |

**What goes where.**
- On the replica, through `ReadStore`: transactions list and lookup, disputes list and lookup, and `/api/me`.
- On the primary:
  - login, because it must see the latest account state;
  - every read inside `DisputeService`: eligibility, the active-dispute check and idempotency replays decide writes, so a lagging replica could let a duplicate through;
  - the decision consumer.

**Retries and idempotency.** A retried `SaveChanges` whose first attempt actually committed (the acknowledgement was lost) hits the idempotency or active-dispute unique index. `DisputeService` already turns that into a replay or a 409, so a retry never creates a second dispute.

**Explicit transactions.** Under a retrying strategy, a `BeginTransaction` must run inside `Database.CreateExecutionStrategy().ExecuteAsync(...)`, so that the whole unit is replayed (see `OutboxRelay`).

## Failure modes

| Failure | What the customer sees | What recovers it |
|---|---|---|
| Primary Postgres blip (failover, dropped connections) | A slower response | EF retries, 3 times with backoff |
| Primary Postgres down | 500 on reads and writes (no source of truth) | Restore the primary. `/health` is Unhealthy |
| Replica down | Nothing; reads are a little slower | `ReadStore` falls back to the primary. `/health` is Degraded |
| Replica lagging | Nothing for their own changes (read-your-writes). Others' changes appear a moment late | Streaming replication catches up |
| Redis down | 503 `session_store_unavailable` (sessions fail closed) | Restore Redis |
| Kafka down | Nothing: raise and withdraw still return 201/200 | The outbox retries with backoff. `/health` is Degraded after 60 s. Events go out in order when Kafka returns |
| API crashes mid-publish | Nothing | Unpublished rows are picked up on restart. A duplicate is possible; consumers dedupe |
| Consumer crashes mid-message | Nothing | No offset commit, so redelivery. The inbox makes it a no-op if the change had committed |
| Same decision delivered twice | One timeline row | Inbox primary key on `eventId` |
| Decision out of order or late | No wrong status | Stale events are skipped; early ones go to the DLQ |
| Poison message | Nothing | DLQ with a reason header; the partition keeps flowing |

## Try it locally

```bash
docker compose --profile tools up --build -d    # API, db + db-replica, redis, kafka (+ kafka-ui on :8081)
cd web && docker compose up --build -d          # portal on :3000

# Raise a dispute at :3000 (thandi@example.com / Demo@12345!), note its number, then play the back office:
scripts/backoffice.sh DSP-2026-001025 under-review
scripts/backoffice.sh DSP-2026-001025 refund-issued "Refund of R10.00 issued to your card."
# Refresh the dispute in the portal: the timeline shows both. kafka-ui (:8081) shows the messages.
```

### The agent login

The same flow without a terminal: sign in at :3000 as `agent@example.com` (password `Demo@12345!`) in a private window, alongside the customer. The login is local compose only (`Agent:Enabled`).

The agent page doesn't change disputes. It publishes each decision through the outbox to the topic it would come from in real life, and the consumer judges it. The page warns about moves that will fail, but sends them anyway so you can watch what happens:

| On a Submitted dispute, send | Kafka UI (:8081) shows | The dispute |
|---|---|---|
| Refund issued | `dispute-refund-issued`, then `dispute-decisions-dlq` with a `dlq-reason: transition_not_allowed` header | Doesn't change |
| Under review | `dispute-under-review` | → Under review |
| Refund issued | `dispute-refund-issued` | → Resolved |
| Rejected (now it's Resolved) | `dispute-rejected`, and the API logs that it's stale | Doesn't change |

The customer sees each applied decision on the dispute's timeline after a refresh. To replay an event id and show it's only applied once, use `EVENT_ID` with `scripts/backoffice.sh`, as in the drills below.

Drills:
- **Kafka down:**
  - `docker compose stop kafka`, then raise a dispute: it returns 201.
  - The `OutboxMessages` row shows `Attempts` and `LastError`, and `/health` goes Degraded after 60 seconds.
  - `docker compose start kafka`: the row is published.
- **Replica down:**
  - `docker compose stop db-replica`: lists still load.
  - The API logs "Read replica unavailable … served … from the primary", and `/health` is Degraded.
- **Duplicate delivery:** `EVENT_ID=<an id the script printed> scripts/backoffice.sh … refund-issued` logs "already handled; skipped".

## What production would add

- **Schema Registry** with Avro or Protobuf instead of JSON, so `schemaVersion` changes are checked at publish time.
- **Durability:**
  - 3 brokers, replication factor 3, `min.insync.replicas=2`;
  - TLS and SASL with per-service ACLs: the API may only write `dispute-submitted` and `dispute-withdrawn`, and only read the decision topics.
- **Alerting** on DLQ depth, outbox age (the `outbox` health check), consumer lag and replica lag.
- **Database:**
  - a dedicated replication role and a read-only role for the replica connection;
  - more replicas behind a pooler such as PgBouncer.
- **Housekeeping:** a cleanup job for published outbox rows and old inbox rows (both grow forever here).
- **Optionally, change data capture** (Debezium reading the outbox table) instead of polling, for lower latency without the 1-second poll.
