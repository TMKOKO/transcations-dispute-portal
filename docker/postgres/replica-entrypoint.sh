#!/bin/sh
# db-replica: a hot standby of db. On first start (empty data folder) it clones the primary with
# pg_basebackup; -R writes standby.signal and primary_conninfo, so Postgres then starts as a
# read-only replica streaming WAL from db. Later starts reuse the clone and catch up.
set -eu

if [ ! -s "$PGDATA/PG_VERSION" ]; then
  echo "db-replica: cloning $PRIMARY_HOST"
  mkdir -p "$PGDATA"
  chown postgres:postgres "$PGDATA"
  chmod 700 "$PGDATA"
  su-exec postgres pg_basebackup -h "$PRIMARY_HOST" -U "$PRIMARY_USER" -D "$PGDATA" -R -X stream
fi

exec docker-entrypoint.sh postgres -c hot_standby=on
