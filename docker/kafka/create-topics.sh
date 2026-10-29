#!/bin/sh
# kafka-init: creates the dispute topics (src/DisputePortal.Api/Events/DisputeTopics.cs), then exits.
# 3 partitions each; messages are keyed by dispute id, so one dispute's events stay in order.
# Replication factor 1 because the local stack has one broker; production uses 3 with min.insync.replicas=2.
set -eu

for topic in \
  dispute-submitted \
  dispute-withdrawn \
  dispute-under-review \
  dispute-refund-issued \
  dispute-rejected \
  dispute-decisions-dlq
do
  /opt/kafka/bin/kafka-topics.sh --bootstrap-server "$BOOTSTRAP_SERVERS" \
    --create --if-not-exists --topic "$topic" --partitions 3 --replication-factor 1
done

/opt/kafka/bin/kafka-topics.sh --bootstrap-server "$BOOTSTRAP_SERVERS" --list
