# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

Transactions Dispute Portal: an ASP.NET Core 8 API (`src/DisputePortal.Api`, PostgreSQL 16 via EF Core/Npgsql, Redis 7 for sessions) and a separate React customer portal (`web/`). Customers sign in, browse their own transactions, raise disputes and withdraw them. Reviewing disputes (Under review / Resolved / Rejected) is the bank's back office and deliberately out of scope; there is no agent API.

## Commands

API (repo root; .NET SDK 8.0.206 per `global.json`):

```bash
dotnet build DisputePortal.sln                      # TreatWarningsAsErrors is on
dotnet test tests/DisputePortal.UnitTests
dotnet test tests/DisputePortal.IntegrationTests    # needs Docker running (Testcontainers starts postgres:16-alpine and redis:7-alpine)
dotnet test tests/DisputePortal.IntegrationTests --filter "FullyQualifiedName~DisputesTests.Dispute_ids_travel_in_the_body_not_the_url"
dotnet tool restore && dotnet ef migrations add <Name> --project src/DisputePortal.Api --output-dir Data/Migrations
```

Run the two test projects separately rather than the whole solution at once; on this machine a combined run has been killed for low memory.

Local stack:

```bash
docker compose up --build -d            # repo root: db (host port 5433) + redis (host port 6380) + API on http://localhost:8080 (/swagger, /health)
cd web && docker compose up --build -d  # portal on http://localhost:3000 (start the API first)
docker compose -f docker-compose.yml -f docker-compose.observability.yml up --build -d   # + Grafana on :3001 (admin/admin), Loki, Alloy
```

The observability overlay (`observability/README.md`) switches the API's logs to JSON. Dashboard panels match log lines by their message text (for example `|= "signed in; session"`, `|= "failed with"`), so rewording a log message in the API means updating `observability/grafana/dashboards/dispute-portal.json` too. Grafana's `grafana_reader` database role has column-level grants (`observability/postgres/grafana-reader.sql`); a new panel column needs a new grant there.

Web (`web/`, Node >= 20.11):

```bash
npm run dev          # Vite on :5173 (already in the API's CORS list)
npm test             # vitest + msw, jsdom
npx vitest run src/pages/LoginPage.test.tsx
npm run typecheck && npm run lint
```

Postman: `docs/postman/` (collection + local environment, see its README). Headless run: `npx newman@6.2.1 run docs/postman/DisputePortal.postman_collection.json -e docs/postman/DisputePortal.local.postman_environment.json`.

Demo data is seeded only when `ASPNETCORE_ENVIRONMENT=Development` (as in compose): customers such as `thandi@example.com` and `sipho@example.com`, all with password `Demo@12345!`. `DbSeeder` gives every customer the same fixed scenario transactions plus random ones, so different customers see similar rows.

## Architecture

**Request flow.** Controllers are thin HTTP shells (routing, auth attributes, `User.GetCustomerId()`, cookies and headers, results to responses). Each has a ViewModel in `ViewModels/` (`AuthViewModel`, `MeViewModel`, `TransactionsViewModel`, `DisputesViewModel`) that owns its reads (plain EF queries), logging and DTO mapping; keep new endpoints to that split. `DisputeService` owns every dispute write (raise, withdraw); `DisputesViewModel` delegates to it. ViewModels and services return `ServiceResult<T>` with a `ServiceError(Kind, Code, Message)`. Controllers convert errors with `this.ToProblem(error)` (`Helpers/ProblemResults.cs`): NotFound→404, Conflict→409, BusinessRule→422. Responses are RFC 7807 bodies with a stable `code` extension that the portal switches on. Return errors through `ProblemWithCode`/`ToProblem` rather than `NotFound()` etc., because that helper is also the central failure log.

**Sessions.** Server-side, in Redis; there is no JWT.
- Login (`AuthController`) creates a session via `RedisSessionStore` (`Services/SessionStore.cs`) and sets its random id in the `dp_session` cookie: httpOnly, SameSite=Strict, Secure (off only in local compose/Development via `Session:SecureCookie`), no Max-Age. The response body carries no token.
- Redis key is `session:{sha256(id)}`, so neither the id nor the key is ever logged (`LoggingTests` asserts this).
- `SessionAuthenticationHandler` (`Auth/`) authenticates each request with one `GETEX`, which also restarts the idle timer (`Session:IdleTimeout`, 5 min). `Session:AbsoluteLifetime` (8 h) caps a session however busy it is, using the injected `TimeProvider`.
- It builds claims `role=customer`, `customerId`, `sub` (`Auth/SessionClaims.cs`); `User.GetCustomerId()` reads them. No session gives 401 `unauthenticated` and clears the cookie.
- Redis unreachable fails closed: `SessionStoreUnavailableHandler` returns 503 `session_store_unavailable`, never 401, and `/health` checks Redis.
- `POST /api/auth/logout` deletes the session and is always 204. Login deletes any session the browser already presented (session fixation).
- `OriginCheckMiddleware` rejects state-changing requests from an `Origin` that is neither the API itself nor in `Cors:AllowedOrigins` (403 `origin_not_allowed`).
- Portal and API must be same-site in production (shared registrable domain) because of SameSite=Strict.

**Customer isolation.** `User.GetCustomerId()` reads the customer id from the session's claims. Every query filters on that customerId, and another customer's record returns 404, the same as a missing one, never 403. Keep this for any new endpoint.

**IDs never go in URLs.** Single-item operations are POSTs with the ID in the JSON body: `POST /api/transactions/lookup`, `/api/disputes/lookup`, `/api/disputes/withdraw`. Raising a dispute returns 201 with no `Location` header. Integration tests assert the old GET routes are 404. The portal follows the same rule for its own pages (`/transactions/dispute`, `/disputes/detail`): the ID travels in React Router state via `raiseDisputeLink`/`disputeLink`/`useStateId` in `web/src/lib/routes.ts`. A page opened without that state goes back to its list.

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

**Dates.** Stored in UTC; customers filter by SAST calendar dates (`Helpers/Sast.cs`, fixed UTC+2). "Now" always comes from the injected `TimeProvider` so tests can pin it.

**Cross-cutting (Program.cs).**
- One global partitioned fixed-window rate limiter: `/api/auth/login` gets 10/min, everything else (including logout) 300/min. Both are configurable, and `TrustForwardedFor` is off by default.
- Model-validation 400s are logged by field name in `InvalidModelStateResponseFactory`.
- JSON console logging outside Development.
- `SecurityHeadersMiddleware`, 64 KB body limit, CORS policy "Portal" from `Cors:AllowedOrigins` (with credentials, for the session cookie).
- Swagger and seeding run in Development only.
- Migrations are applied at startup.
- Login uses a dummy BCrypt hash for unknown emails so timing does not reveal which accounts exist.

**Logging conventions.**
- Use structured `ILogger` templates: Information after every DB call and on success, Warning on failures.
- Log IDs, codes, counts, statuses and amounts only. Never log emails, passwords, session ids (or their Redis keys), dispute descriptions, withdraw notes or search text. `LoggingTests` asserts this using `FakeLogCollector` (`ApiFactory.Logs`).
- Login failures log the internal reason, but the client always gets the same generic 401.

**Tests.**
- Unit tests cover eligibility, the state machine and request validation.
- Integration tests use `ApiFactory` (`WebApplicationFactory<Program>`). It runs in the "Testing" environment: no seed, no Swagger, rate limits raised.
- All tests share one Postgres and one Redis container (`ContainersFixture`) via `[Collection(ApiCollection.Name)]`. `CreateCustomerAsync` gives each test its own customer.
- Clients use `https://localhost` so the Secure cookie flows. `CustomerClientAsync` signs in through the client's cookie container. `ClientWithSession(id)` sends a raw session id, and `ApiFactory.Redis` asserts on keys and TTLs.

**Web portal.**
- React 18 + Vite, React Router 7, TanStack Query (`src/api/queries.ts`) and Tailwind 4.
- `src/api/client.ts` is the fetch wrapper that turns ProblemDetails into `ApiError`.
- `fetch` uses `credentials: 'include'`; the portal never sees the session id. `sessionStorage` (`src/auth/AuthContext.tsx`) holds only the display name and the idle deadline.
- Every API response (except 401/503 and the login/logout calls) pushes the deadline back, mirroring the server's idle timer. The portal signs out at that deadline or on any 401. Sign out calls `POST /api/auth/logout`.
- Only the customer may cause API calls: refetch on window focus or reconnect is off (`src/api/queryClient.ts`), because any request keeps the session alive. The deadline is also checked when the tab becomes visible or focused, because background-tab timers fire late.
- A minute before the deadline `IdleWarningDialog` (in `Layout`) asks "Are you still there?" with a countdown. "Stay signed in" (or Escape) calls `GET /api/me` to restart the timer; "Sign out" signs out at once.
- `VITE_API_BASE` is baked in at build time.
- Form rules in `src/lib/disputeRules.ts` mirror `RaiseDisputeRequest` in `DTOs/DisputeDtos.cs`; change both together.
- Tests mock the API with msw handlers in `src/test/server.ts`. Update the handlers when routes change.

## Conventions

- `.editorconfig`: LF line endings, 4-space C#, 2-space JSON/TS/MD, file-scoped namespaces.
- Use exact package versions (npm and NuGet 8.0.x).
- Real secrets come from the environment (`.env.example` shows production-shaped settings, including log-level overrides). `Session__SecureCookie: "false"` in `docker-compose.yml` is for the local http stack only.
- `.claude/agents/` defines `architect-review` (design/ADR only) and `pen-tester` (read-and-test only, against localhost:8080/3000).
