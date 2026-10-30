# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

Transactions Dispute Portal: an ASP.NET Core 8 API (`src/DisputePortal.Api`, PostgreSQL 16 via EF Core/Npgsql with a read replica, Redis 7 for sessions, Kafka for dispute events) and a separate React customer portal (`web/`). Customers sign in, browse their own transactions, raise disputes and withdraw them. Reviewing disputes is the bank's back office and deliberately out of scope. The back office's decisions (Under review / Refund issued → Resolved / Rejected) reach the API only as Kafka events. A demo agent login (off unless `Agent:Enabled`, on only in local compose) plays the back office from the portal, and it follows the same rule: it publishes decisions, it never changes a dispute. `docs/resilience.md` explains the event flow and failure modes.

## Commands

API (repo root; .NET SDK 8.0.206 per `global.json`):

```bash
dotnet build DisputePortal.sln                      # TreatWarningsAsErrors is on
dotnet test tests/DisputePortal.UnitTests
dotnet test tests/DisputePortal.IntegrationTests    # needs Docker running (Testcontainers starts postgres:16-alpine and redis:7-alpine; KafkaTests also apache/kafka:3.8.0)
dotnet test tests/DisputePortal.IntegrationTests --filter "FullyQualifiedName~DisputesTests.Dispute_ids_travel_in_the_body_not_the_url"
dotnet tool restore && dotnet ef migrations add <Name> --project src/DisputePortal.Api --output-dir Data/Migrations --context AppDbContext   # --context: ReadDbContext shares the model
```

Run the two test projects separately rather than the whole solution at once; on this machine a combined run has been killed for low memory. There is no CI (`.github/workflows` is empty), so these local runs are the only check before a PR.

Local stack:

```bash
docker compose up --build -d            # repo root: db (5433) + db-replica (5434) + redis (6380) + kafka (9094) + API on http://localhost:8080 (/swagger, /health)
docker compose --profile tools up -d    # + Kafka UI on http://localhost:8081
cd web && docker compose up --build -d  # portal on http://localhost:3000 (start the API first)
docker compose -f docker-compose.yml -f docker-compose.observability.yml up --build -d   # + Grafana on :3001 (admin/admin), Loki, Alloy
scripts/backoffice.sh DSP-2026-001000 under-review|refund-issued|rejected ["note"]   # play the back office: publish a decision
docker compose up -d db redis && dotnet run --project src/DisputePortal.Api   # API from source on :8080 (Development settings), instead of the compose api: primary only, no replica, Kafka off
```

`docker/postgres/` holds the primary's `pg_hba.conf` (adds a replication line) and `replica-entrypoint.sh` (`pg_basebackup` on first start). `docker/kafka/create-topics.sh` is run by the one-shot `kafka-init` service. Broker auto-create is off, so a new topic goes there and in `Events/DisputeTopics.cs`.

The observability overlay (`observability/README.md`) switches the API's logs to JSON. Dashboard panels match log lines by their message text (for example `|= "signed in; session"`, `|= "failed with"`), so rewording a log message in the API means updating `observability/grafana/dashboards/dispute-portal.json` too. Grafana's `grafana_reader` database role has column-level grants (`observability/postgres/grafana-reader.sql`); a new panel column needs a new grant there.

Web (`web/`, Node >= 20.11):

```bash
npm run dev          # Vite on :5173 (already in the API's CORS list)
npm test             # vitest + msw, jsdom
npx vitest run src/pages/LoginPage.test.tsx
npm run typecheck && npm run lint
npm run build        # tsc + vite build (what the web Dockerfile runs)
```

Postman: `docs/postman/` (collection + local environment, see its README). The collection covers every endpoint, so add new ones to it. Headless run: `npx newman@6.2.1 run docs/postman/DisputePortal.postman_collection.json -e docs/postman/DisputePortal.local.postman_environment.json`.

Demo data is seeded only when `ASPNETCORE_ENVIRONMENT=Development` (as in compose): customers such as `thandi@example.com` and `sipho@example.com`, all with password `Demo@12345!`. `DbSeeder` gives every customer the same fixed scenario transactions plus random ones, so different customers see similar rows. `agent@example.com` (same password) is seeded separately, and only with `Agent:Enabled` on, so a database seeded earlier still gets it.

## Architecture

**Request flow.** Controllers are thin HTTP shells (routing, auth attributes, `User.GetCustomerId()`, cookies and headers, results to responses). Each has a ViewModel in `ViewModels/` (`AuthViewModel`, `MeViewModel`, `TransactionsViewModel`, `DisputesViewModel`) that owns its reads (plain EF queries), logging and DTO mapping; keep new endpoints to that split. `DisputeService` owns every dispute write (raise, withdraw); `DisputesViewModel` delegates to it. ViewModels and services return `ServiceResult<T>` with a `ServiceError(Kind, Code, Message)`. Controllers convert errors with `this.ToProblem(error)` (`Helpers/ProblemResults.cs`): NotFound→404, Conflict→409, BusinessRule→422. Responses are RFC 7807 bodies with a stable `code` extension that the portal switches on. Return errors through `ProblemWithCode`/`ToProblem` rather than `NotFound()` etc., because that helper is also the central failure log. Code outside MVC (authentication, middleware, exception handlers) uses `ProblemResponses.WriteAsync`, which writes the same log line and body.

**Sessions.** Server-side, in Redis; there is no JWT.
- Login (`AuthController`) creates a session via `RedisSessionStore` (`Services/SessionStore.cs`) and sets its random id in the `dp_session` cookie: httpOnly, SameSite=Strict, Secure (off only in local compose/Development via `Session:SecureCookie`), no Max-Age. The response body carries no token.
- Redis key is `session:{sha256(id)}`, so neither the id nor the key is ever logged (`LoggingTests` asserts this).
- `SessionAuthenticationHandler` (`Auth/`) authenticates each request with one `GETEX`, which also restarts the idle timer (`Session:IdleTimeout`, 5 min). `Session:AbsoluteLifetime` (8 h) caps a session however busy it is, using the injected `TimeProvider`.
- It builds claims `role=customer`, `customerId`, `sub` (`Auth/SessionClaims.cs`); `User.GetCustomerId()` reads them. An agent session has `role=agent`, `agentId`, `sub` and no `customerId`. No session gives 401 `unauthenticated` and clears the cookie; a live session in the wrong role gives 403 `forbidden`.
- Redis unreachable fails closed: `SessionStoreUnavailableHandler` returns 503 `session_store_unavailable`, never 401, and `/health` checks Redis.
- `POST /api/auth/logout` deletes the session and is always 204. Login deletes any session the browser already presented (session fixation).
- `OriginCheckMiddleware` rejects state-changing requests from an `Origin` that is neither the API itself nor in `Cors:AllowedOrigins` (403 `origin_not_allowed`).
- Portal and API must be same-site in production (shared registrable domain) because of SameSite=Strict.

**Customer isolation.** `User.GetCustomerId()` reads the customer id from the session's claims. Every query filters on that customerId, and another customer's record returns 404, the same as a missing one, never 403. Keep this for any new endpoint.

**Demo agent.** `AgentController` (`/api/agent`, role `agent`) lists every customer's disputes through `ReadStore.QueryAcrossCustomersAsync`, leaving out the description, withdraw note and customer details. `AgentDecisionService` adds a decision to the outbox and returns 202; the usual `DecisionConsumer` and `DecisionHandler` apply it. It deliberately doesn't check the state machine, so a bad move lands on the DLQ or is skipped as stale, where the demo can show it. With `Agent:Enabled` off, login ignores the `Agents` table and agent sessions in Redis stop working. In the portal, `RequireRole` (`components/Layout.tsx`) keeps each role on its own pages and `lib/agentRules.ts` mirrors the consumer's outcomes to warn before sending.

**IDs never go in URLs.** Single-item operations are POSTs with the ID in the JSON body: `POST /api/transactions/lookup`, `/api/disputes/lookup`, `/api/disputes/withdraw`, `/api/agent/disputes/decision`. Raising a dispute returns 201 with no `Location` header. Integration tests assert the old GET routes are 404. The portal follows the same rule for its own pages (`/transactions/dispute`, `/disputes/detail`): the ID travels in React Router state via `raiseDisputeLink`/`disputeLink`/`useStateId` in `web/src/lib/routes.ts`. A page opened without that state goes back to its list.

**Dispute rules.**
- `Models/Dispute.cs` holds the state machine: `AllowedTransitions`; status changes only via `TransitionTo`, which writes a `DisputeStatusHistory` row.
- `Services/DisputeEligibility.cs` decides whether a transaction can be disputed. The window is `Disputes:WindowDays`, default 120.
- Postgres enforces the invariants, so integration tests use a real database rather than EF in-memory:
  - a filtered unique index allows one active dispute per transaction (`ux_disputes_one_active_per_transaction`);
  - `(CustomerId, IdempotencyKey)` is unique;
  - an `xmin` row version detects concurrent edits;
  - dispute numbers come from `dispute_number_seq`.
- `DisputeService` catches unique violations by constraint name to tell a lost race apart from a replay.
- Raising requires an `Idempotency-Key` header. A replay returns the original dispute with an `Idempotent-Replayed` header.

**Reads (replica).**
- ViewModel reads go through `ReadStore.QueryAsync(customerId, "label", db => …)` (`Data/ReadStore.cs`), never a raw DbContext. Queries must be `AsNoTracking`, and the label is a fixed string.
- `ReadStore` uses `ReadDbContext` on `ConnectionStrings:ReadOnlyConnection` (unset: the primary, and the replica logic is skipped).
- If the replica is unreachable, the read runs once more on the primary and logs "Read replica unavailable".
- After a raise or withdraw, `ReadYourWrites.MarkAsync` sets Redis key `rw:{customerId}` (5 s), and that customer's reads use the primary.
- Anything that decides a write (`DisputeService`, login, `DecisionHandler`) reads `AppDbContext`, the primary.
- Both contexts use `EnableRetryOnFailure`, so an explicit `BeginTransaction` must run inside `Database.CreateExecutionStrategy().ExecuteAsync` (see `OutboxRelay`).

**Dispute events (Kafka, `Events/`).** One topic per event, keyed by dispute id; the constants are in `DisputeTopics.cs`, and the JSON envelope is `DisputeEvent.cs`.
- **Never put the description or a customer's withdraw note in an event** (claim check: consumers read the dispute by id).
- **Outbound** (`dispute-submitted`, `dispute-withdrawn`) uses a transactional outbox:
  - `DisputeService` adds an `OutboxMessage` in the same `SaveChanges` as the change;
  - `OutboxPublisherService` runs `OutboxRelay` (`FOR UPDATE SKIP LOCKED`, in Id order, stops at the first failure) with backoff.
  - Kafka down never fails a request.
- **Inbound** (`dispute-under-review`, `dispute-refund-issued` → Resolved, `dispute-rejected`):
  - `DecisionConsumer` (manual commit after the handler) feeds `DecisionHandler`.
  - The handler applies `TransitionTo(…, DisputeActor.Agent, note)` plus an `InboxMessage` (PK = eventId) in one `SaveChanges`.
  - Duplicates and stale events are skipped. Anything that can never apply is a `DecisionResult.DeadLetter(reason)`, which the consumer sends to `dispute-decisions-dlq` and commits.
  - Exceptions mean "retry": the consumer seeks back and backs off.
- `Kafka:Enabled` is off by default and in tests: outbox rows are still written, but no publisher or consumer runs.
- `/health` reports the `outbox` (registered only with Kafka on) and `read-replica` (only with a replica configured) checks as Degraded, never Unhealthy.

**Dates.** Stored in UTC; customers filter by SAST calendar dates (`Helpers/Sast.cs`, fixed UTC+2). "Now" always comes from the injected `TimeProvider` so tests can pin it.

**Cross-cutting (Program.cs).**
- One global partitioned fixed-window rate limiter: `/api/auth/login` gets 10/min, everything else (including logout) 300/min. Both are configurable, and `TrustForwardedFor` is off by default.
- Model-validation 400s are logged by field name in `InvalidModelStateResponseFactory`.
- JSON console logging outside Development.
- `SecurityHeadersMiddleware`, 64 KB body limit, CORS policy "Portal" from `Cors:AllowedOrigins` (with credentials, for the session cookie). Its methods, request headers and exposed response headers are explicit lists, so a new one must be added there or the browser blocks it.
- Swagger and seeding run in Development only.
- Migrations are applied at startup.
- Login uses a dummy BCrypt hash for unknown emails so timing does not reveal which accounts exist.

**Logging conventions.**
- Use structured `ILogger` templates: Information after every DB call and on success, Warning on failures.
- Log IDs, codes, counts, statuses and amounts only. Never log emails, passwords, session ids (or their Redis keys), dispute descriptions, withdraw notes or search text. `LoggingTests` asserts this using `FakeLogCollector` (`ApiFactory.Logs`).
- Login failures log the internal reason, but the client always gets the same generic 401.

**Validation.**
- Request DTOs use DataAnnotations plus `IValidatableObject` for cross-field and trimmed-length rules.
- Every free-text field carries `[PlainText]` (`Helpers/PlainTextAttribute.cs`), which rejects control characters and bidi overrides. Use `AllowLineBreaks = true` for multi-line text (description, withdraw note).
- Validation fails fast. `InvalidModelStateResponseFactory` in `Program.cs` trims each field to the first rule it broke and re-keys `IValidatableObject` results from C# names (`Note`) to JSON names (`note`). So a 400's `errors` has exactly one message per field, under the name the client sent.

**Tests.**
- Unit tests cover eligibility, the state machine, request validation and the event envelope.
- Integration tests use `ApiFactory` (`WebApplicationFactory<Program>`). It runs in the "Testing" environment: no seed, no Swagger, rate limits raised.
- All tests share one Postgres and one Redis container (`ContainersFixture`) via `[Collection(ApiCollection.Name)]`. `CreateCustomerAsync` gives each test its own customer.
- `EventsTests` covers the outbox, `OutboxRelay` (with a fake `IEventPublisher`) and `DecisionHandler` directly, with no broker.
- `KafkaTests` is the one class with a real broker: an `IClassFixture<KafkaFixture>` running `apache/kafka`, with `ApiFactory(kafkaBootstrapServers:)`.
- `ApiFactory(readOnlyConnectionString:)` sets up a replica: either the test database, or a dead port for failover tests.
- The outbox table is shared, so a test asserts on its own dispute's rows (`Key` = dispute id).
- Clients use `https://localhost` so the Secure cookie flows. `CustomerClientAsync` signs in through the client's cookie container. `ClientWithSession(id)` sends a raw session id, and `ApiFactory.Redis` asserts on keys and TTLs.

**Web portal.**
- React 18 + Vite, React Router 7, TanStack Query (`src/api/queries.ts`) and Tailwind 4.
- `src/api/client.ts` is the fetch wrapper that turns ProblemDetails into `ApiError`.
- `fetch` uses `credentials: 'include'`; the portal never sees the session id. `sessionStorage` (`src/auth/AuthContext.tsx`) holds only the display name and the idle deadline.
- Every API response (except 401/503 and the login/logout calls) pushes the deadline back, mirroring the server's idle timer. The portal signs out at that deadline or on any 401. Sign out calls `POST /api/auth/logout`.
- Tabs in one browser share the cookie but not `sessionStorage`, so a second account signing in takes over every tab. A 403 `forbidden` means exactly that (`RequireRole` keeps each role on its own pages): the tab signs itself out and explains, without calling logout, which would end the other account's session.
- Only the customer may cause API calls: refetch on window focus or reconnect is off (`src/api/queryClient.ts`), because any request keeps the session alive. The one exception is the agent page, which polls for up to 10 s after the agent sends a decision. The deadline is also checked when the tab becomes visible or focused, because background-tab timers fire late.
- A minute before the deadline `IdleWarningDialog` (in `Layout`) asks "Are you still there?" with a countdown. "Stay signed in" (or Escape) calls `GET /api/me` (agents: `GET /api/agent/session`) to restart the timer; "Sign out" signs out at once.
- Dates are shown and typed day first. Display goes through `src/lib/format.ts` (en-ZA, SAST). Date inputs use `components/DateField.tsx` (dd/mm/yyyy, via `isoToDmy`/`dmyToIso`), never a bare `<input type="date">`, which follows the browser's language (mm/dd/yyyy in US English). URLs and the API keep `yyyy-MM-dd`.
- `VITE_API_BASE` is baked in at build time.
- Form rules (zod schemas) mirror the API DTOs, so problems show before any request; change both sides together.
  - `src/lib/disputeRules.ts` mirrors `RaiseDisputeRequest`, `WithdrawDisputeRequest` and `PlainTextAttribute` (`isPlainText`).
  - `src/lib/loginRules.ts` mirrors `LoginRequest`.
  - Forms show one message per field (`FieldError` in `components/ui.tsx`) and focus the first bad field.
- Tests mock the API with msw handlers in `src/test/server.ts`. Update the handlers when routes change.

## Conventions

- `.editorconfig`: LF line endings, 4-space C#, 2-space JSON/TS/MD, file-scoped namespaces.
- Use exact package versions (npm and NuGet 8.0.x).
- Real secrets come from the environment (`.env.example` shows production-shaped settings, including log-level overrides). `Session__SecureCookie: "false"` in `docker-compose.yml` is for the local http stack only.
- `.claude/agents/` defines `architect-review` (design/ADR only) and `pen-tester` (read-and-test only, against localhost:8080/3000).
