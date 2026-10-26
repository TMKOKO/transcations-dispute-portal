-- Read-only role for Grafana, granted column by column: no emails, names, password hashes,
-- dispute descriptions, withdraw notes or idempotency keys. A panel that selects one of those
-- fails with "permission denied" instead of putting personal data on a dashboard.

SELECT 'CREATE ROLE grafana_reader LOGIN'
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'grafana_reader') \gexec

ALTER ROLE grafana_reader WITH LOGIN PASSWORD :'reader_password';
ALTER ROLE grafana_reader SET default_transaction_read_only = on;
ALTER ROLE grafana_reader SET statement_timeout = '10s';

GRANT CONNECT ON DATABASE disputes TO grafana_reader;
GRANT USAGE ON SCHEMA public TO grafana_reader;
REVOKE ALL ON ALL TABLES IN SCHEMA public FROM grafana_reader;

GRANT SELECT ("Id", "CreatedAt") ON "Customers" TO grafana_reader;

GRANT SELECT ("Id", "CustomerId", "MerchantCategory", "Amount", "Currency", "Type", "Channel", "Status", "TransactionDate")
  ON "Transactions" TO grafana_reader;

GRANT SELECT ("Id", "DisputeNumber", "CustomerId", "TransactionId", "Reason", "DisputedAmount", "Status",
              "CreatedAt", "UpdatedAt", "ResolvedAt")
  ON "Disputes" TO grafana_reader;

GRANT SELECT ("Id", "DisputeId", "FromStatus", "ToStatus", "ChangedBy", "ChangedAt")
  ON "DisputeStatusHistory" TO grafana_reader;
