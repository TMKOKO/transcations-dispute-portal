# Local dashboards (Grafana)

Grafana with two data sources, added on top of the API stack:

- **Disputes DB**: the API's PostgreSQL, read through a read-only `grafana_reader` role.
- **Loki**: the API's structured logs, shipped from the container by Grafana Alloy.

## Start it

From the repository root:

```bash
docker compose -f docker-compose.yml -f docker-compose.observability.yml up --build -d
```

Open http://localhost:3001 and sign in as `admin` / `admin`. The **Dispute Portal** dashboard is the home page.

To stop, run the same command with `down` in place of `up --build -d`. Add `-v` to also delete the stored logs, the Grafana state and the database.

The base `docker compose up` still works without any of this.

## What is on the dashboard

| Section | Source | Panels |
|---|---|---|
| Disputes | PostgreSQL | open disputes, amount under dispute, raised and withdrawn in the time range, customers, by status, by reason, raised per day (last 120 days), oldest open disputes |
| API activity | Loki | sign-ins vs failed logins, failures by error `code`, disputes raised / withdrawn / replayed, 429s by limit, races and concurrent updates, validation failures by field, log volume by level |
| Logs | Loki | warnings and errors, and all API logs. Type a TraceId or CustomerId into **Filter logs** to follow one request or one customer. |

The log panels only cover what happened after the stack started. Drive some traffic with the portal (http://localhost:3000) or the Postman collection:

```bash
npx newman@6.2.1 run docs/postman/DisputePortal.postman_collection.json -e docs/postman/DisputePortal.local.postman_environment.json
```

## How it fits together

- `docker-compose.observability.yml` switches the API's console logs to JSON (`Logging__Console__FormatterName=json`). Outside Development, `Program.cs` already does this.
- `alloy/config.alloy` collects only the compose service named `api`. It adds the labels `service`, `level` and `category`.
  - Fields such as `Code`, `CustomerId` and `TraceId` stay in the log line.
  - Queries extract them with `| json code="State.Code"`.
- `postgres/grafana-reader.sql` grants column-level `SELECT` only. Grafana cannot read:
  - emails, names or password hashes
  - dispute descriptions or withdraw notes
  - idempotency keys

  The role is also read-only, with a 10-second statement timeout. It is recreated on every start by the one-shot `grafana-db-user` service.
- `grafana/provisioning/` sets up both data sources and loads `grafana/dashboards/dispute-portal.json`.
  - You can edit the dashboard in the UI.
  - To keep your changes, export the JSON over that file.

## Settings

| Variable | Default | Used for |
|---|---|---|
| `GRAFANA_ADMIN_PASSWORD` | `admin` | Grafana `admin` login |
| `GRAFANA_DB_PASSWORD` | `grafana-local-only` | `grafana_reader` database password |

This setup is for local use only. Alloy mounts the Docker socket, Loki stores on the local disk (7-day retention), and the passwords are defaults.

## Not included

Request rates, latencies and CPU/memory need metrics (OpenTelemetry or Prometheus). They are not part of this setup.
