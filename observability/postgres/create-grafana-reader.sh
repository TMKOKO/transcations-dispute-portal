#!/bin/sh
# Waits for the API to migrate the schema, then (re)creates the grafana_reader role.
# Safe to run on every start: the SQL is idempotent.
set -eu

echo "Waiting for the Disputes table..."
until [ "$(psql -tAc "SELECT to_regclass('public.\"Disputes\"') IS NOT NULL")" = "t" ]; do
  sleep 2
done

psql -v ON_ERROR_STOP=1 -v reader_password="$GRAFANA_DB_PASSWORD" -f /scripts/grafana-reader.sql
echo "grafana_reader is ready"
