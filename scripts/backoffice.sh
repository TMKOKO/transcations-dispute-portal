#!/usr/bin/env bash
# Plays the bank's back office on the local stack: publishes a decision on a dispute to Kafka, which
# the API's DecisionConsumer applies to the dispute and its timeline.
#
#   scripts/backoffice.sh DSP-2026-001000 under-review
#   scripts/backoffice.sh DSP-2026-001000 refund-issued "Refund of R250.00 issued to your card."
#   scripts/backoffice.sh DSP-2026-001000 rejected "The card and PIN were used."
#
# The dispute is its number (as the portal shows it) or its id. Set EVENT_ID to reuse an event id,
# which shows that a redelivered event is applied only once.
set -euo pipefail
cd "$(dirname "$0")/.."
# Git Bash on Windows would otherwise rewrite /opt/kafka/... into a Windows path.
export MSYS_NO_PATHCONV=1

usage() { sed -n '2,10p' "$0" | sed 's/^# \{0,1\}//'; exit 2; }
[ $# -ge 2 ] || usage

dispute=$1 decision=$2 note=${3:-}
case $decision in
  under-review)  event_type=UnderReview ;;
  refund-issued) event_type=RefundIssued ;;
  rejected)      event_type=Rejected ;;
  *) usage ;;
esac

if [[ $dispute =~ ^DSP-[0-9]{4}-[0-9]+$ ]]; then
  # Validated above, so the number is safe to put in the SQL.
  dispute_id=$(docker compose exec -T db psql -U postgres -d disputes -tAc \
    "SELECT \"Id\" FROM \"Disputes\" WHERE \"DisputeNumber\" = '$dispute'")
  [ -n "$dispute_id" ] || { echo "No dispute $dispute" >&2; exit 1; }
elif [[ $dispute =~ ^[0-9a-fA-F-]{36}$ ]]; then
  dispute_id=$dispute
else
  usage
fi

event_id=${EVENT_ID:-$(docker compose exec -T kafka cat /proc/sys/kernel/random/uuid | tr -d '\r')}
occurred_at=$(date -u +%Y-%m-%dT%H:%M:%SZ)

json_escape() { printf '%s' "$1" | sed 's/\\/\\\\/g; s/"/\\"/g'; }
event="{\"eventId\":\"$event_id\",\"eventType\":\"$event_type\",\"schemaVersion\":1,\"occurredAt\":\"$occurred_at\",\"disputeId\":\"$dispute_id\""
[ -n "$note" ] && event+=",\"note\":\"$(json_escape "$note")\""
event+="}"

# Keyed by dispute id, like every dispute event, so one dispute's decisions stay in order.
printf '%s|%s\n' "$dispute_id" "$event" | docker compose exec -T kafka /opt/kafka/bin/kafka-console-producer.sh \
  --bootstrap-server kafka:9092 --topic "dispute-$decision" \
  --property parse.key=true --property key.separator='|'

echo "Sent $event_type for dispute $dispute_id (event $event_id) to dispute-$decision"
