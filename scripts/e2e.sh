#!/usr/bin/env bash
# Browser smoke tests against the whole stack: portal, API, Postgres, Redis and Kafka.
#
#   scripts/e2e.sh                   # all of them
#   scripts/e2e.sh --grep "Kafka"    # arguments go to Playwright
#
# The stack runs under its own compose project names, with its own volumes, and is removed afterwards,
# so the demo's data is never touched. It needs the same fixed ports as the demo, so the demo has to
# be stopped first: `docker compose stop` in the repo root and in web/ keeps its data.
set -euo pipefail
cd "$(dirname "$0")/.."
ROOT=$PWD

API_PROJECT=dispute-e2e
WEB_PROJECT=dispute-e2e-web

# Asks Docker rather than trying the ports: something else on the machine may share one harmlessly.
if holding=$(docker ps --format '{{.Names}} {{.Ports}}' | grep -E ':(3000|5433|5434|6380|8080|9094)->'); then
  echo "These containers hold ports the test stack needs. Stop them first: docker compose stop (in the repo root and in web/)." >&2
  echo "$holding" >&2
  exit 1
fi

cleanup() {
  echo "Removing the test stack..."
  # The tests run from web/, so this can't rely on the current directory.
  (cd "$ROOT/web" && docker compose -p "$WEB_PROJECT" down -v --remove-orphans) || true
  (cd "$ROOT" && docker compose -p "$API_PROJECT" -f docker-compose.yml -f docker-compose.e2e.yml down -v --remove-orphans) || true
}
trap cleanup EXIT

docker compose -p "$API_PROJECT" -f docker-compose.yml -f docker-compose.e2e.yml up --build -d
(cd web && docker compose -p "$WEB_PROJECT" up --build -d)

wait_for() {
  for _ in $(seq 1 90); do
    if curl -fsS -o /dev/null "$1"; then return 0; fi
    sleep 2
  done
  echo "$1 did not come up within 3 minutes." >&2
  return 1
}
wait_for http://localhost:8080/health
wait_for http://localhost:3000/

cd web
npx playwright install chromium
npx playwright test "$@"
