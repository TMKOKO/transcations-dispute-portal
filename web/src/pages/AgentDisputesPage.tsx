import { useEffect, useState } from 'react'
import { useSearchParams } from 'react-router'
import { useAgentDisputes, useSendDecision } from '../api/queries'
import type { AgentDecisionResult, AgentDispute, DisputeDecision, DisputeStatus } from '../api/types'
import { ConfirmDialog } from '../components/ConfirmDialog'
import { Card, EmptyState, ErrorBanner, FieldError, PageHeader, Pagination, Spinner, StatusBadge } from '../components/ui'
import { DECISION_NOTE_MAX, decisionLabel, decisionNoteSchema, decisionOutcome } from '../lib/agentRules'
import { formatDate, formatMoney } from '../lib/format'
import { statusLabel } from '../lib/labels'
import { useDocumentTitle } from '../lib/useDocumentTitle'

const FILTERS: (DisputeStatus | undefined)[] = [undefined, 'Submitted', 'UnderReview', 'Resolved', 'Rejected', 'Withdrawn']
const DECISIONS: DisputeDecision[] = ['UnderReview', 'RefundIssued', 'Rejected']

const WATCH_POLL_MS = 1_000
const WATCH_FOR_MS = 10_000

// Where the "Open in Kafka UI" link goes, ending at the cluster's topics. Set on local demo builds only.
const kafkaUiTopics = import.meta.env.VITE_KAFKA_UI_URL?.replace(/\/$/, '')

interface Watch {
  disputeId: string
  fromStatus: DisputeStatus
}

export function AgentDisputesPage() {
  useDocumentTitle('All disputes')
  const [params, setParams] = useSearchParams()
  const status = (params.get('status') as DisputeStatus | null) ?? undefined
  const page = Math.max(1, Number(params.get('page')) || 1)

  // A decision comes back through Kafka a moment after it is sent, so the list checks for it. This
  // is the one exception to "only the customer causes API calls": the agent's click starts it, and
  // it stops at the status change or after WATCH_FOR_MS, so it cannot keep an idle session alive.
  const [watch, setWatch] = useState<Watch | null>(null)
  const query = useAgentDisputes(status, page, watch ? WATCH_POLL_MS : undefined)
  const [deciding, setDeciding] = useState<AgentDispute | null>(null)
  const [sent, setSent] = useState<AgentDecisionResult | null>(null)

  useEffect(() => {
    if (!watch) return
    const timer = window.setTimeout(() => setWatch(null), WATCH_FOR_MS)
    return () => window.clearTimeout(timer)
  }, [watch])

  const items = query.data?.items
  useEffect(() => {
    if (!watch || !items) return
    // Gone from the list counts as moved: it no longer matches the status filter.
    const row = items.find((d) => d.id === watch.disputeId)
    if (!row || row.status !== watch.fromStatus) setWatch(null)
  }, [watch, items])

  function setFilter(next: DisputeStatus | undefined) {
    setParams(next ? { status: next } : {}, { replace: true })
  }

  return (
    <>
      <PageHeader
        title="All disputes"
        description="Decisions go to Kafka first. The consumer then applies them, skips them or sends them to the dead-letter topic."
      />

      <div role="group" aria-label="Filter by status" className="mb-4 flex flex-wrap gap-2">
        {FILTERS.map((f) => (
          <button
            key={f ?? 'all'}
            type="button"
            aria-pressed={status === f}
            onClick={() => setFilter(f)}
            className={`rounded-full px-3 py-1.5 text-sm font-medium ring-1 ring-inset transition-colors ${
              status === f ? 'bg-brand-700 text-white ring-brand-700' : 'bg-white text-slate-700 ring-slate-300 hover:bg-slate-50'
            }`}
          >
            {f ? statusLabel[f] : 'All'}
          </button>
        ))}
      </div>

      {sent && (
        <div role="status" className="mb-4 rounded-lg border border-emerald-200 bg-emerald-50 p-4 text-sm text-emerald-900">
          <p>
            Sent <span className="font-medium">{sent.eventType}</span> to <code>{sent.topic}</code>.
            {kafkaUiTopics && (
              <>
                {' '}
                <a href={`${kafkaUiTopics}/${sent.topic}`} target="_blank" rel="noreferrer" className="underline underline-offset-2">
                  Open in Kafka UI
                </a>
              </>
            )}
          </p>
          <p className="mt-1 break-all text-xs text-emerald-800">Event {sent.eventId}</p>
        </div>
      )}

      {query.isPending ? (
        <Spinner label="Loading disputes" />
      ) : query.isError ? (
        <ErrorBanner message={query.error.message} onRetry={() => void query.refetch()} />
      ) : query.data.items.length === 0 ? (
        <EmptyState title={status ? `No ${statusLabel[status].toLowerCase()} disputes` : 'No disputes yet'} />
      ) : (
        <Card>
          <ul aria-label="Disputes" className="divide-y divide-slate-100">
            {query.data.items.map((d) => (
              <li
                key={d.id}
                className="grid grid-cols-[1fr_auto] items-center gap-x-4 gap-y-2 px-4 py-4 sm:grid-cols-[9rem_1fr_auto_7rem_auto]"
              >
                <span className="font-mono text-sm text-slate-700">{d.disputeNumber}</span>
                <span className="col-span-2 min-w-0 sm:col-span-1">
                  <span className="block truncate font-medium text-slate-900">{d.merchantName}</span>
                  <span className="block truncate text-xs text-slate-500">Raised {formatDate(d.createdAt)}</span>
                </span>
                <span className="text-sm font-medium tabular-nums text-slate-900">{formatMoney(d.disputedAmount, d.currency)}</span>
                <span className="row-start-1 text-right sm:row-start-auto">
                  <StatusBadge status={d.status} />
                </span>
                <button
                  type="button"
                  className="btn-secondary"
                  aria-label={`Update status of ${d.disputeNumber}`}
                  onClick={() => setDeciding(d)}
                >
                  Update status
                </button>
              </li>
            ))}
          </ul>
          <Pagination
            page={query.data.page}
            totalPages={query.data.totalPages}
            totalCount={query.data.totalCount}
            onChange={(p) => setParams({ ...(status ? { status } : {}), page: String(p) }, { replace: true })}
          />
        </Card>
      )}

      {deciding && (
        <DecisionDialog
          dispute={deciding}
          onCancel={() => setDeciding(null)}
          onSent={(result) => {
            setSent(result)
            setWatch({ disputeId: deciding.id, fromStatus: deciding.status })
            setDeciding(null)
          }}
        />
      )}
    </>
  )
}

function DecisionDialog({
  dispute,
  onCancel,
  onSent,
}: {
  dispute: AgentDispute
  onCancel: () => void
  onSent: (result: AgentDecisionResult) => void
}) {
  const send = useSendDecision()
  // Starts on a move that applies, so the usual path needs no thought; the others are a choice.
  const [decision, setDecision] = useState<DisputeDecision>(
    () => DECISIONS.find((d) => decisionOutcome(dispute.status, d) === 'applies') ?? 'UnderReview',
  )
  const [note, setNote] = useState('')
  const [noteError, setNoteError] = useState<string | null>(null)

  async function onConfirm() {
    const parsed = decisionNoteSchema.safeParse(note)
    if (!parsed.success) {
      setNoteError(parsed.error.issues[0]?.message ?? 'Check this note.')
      document.getElementById('decision-note')?.focus()
      return
    }
    setNoteError(null)
    try {
      onSent(await send.mutateAsync({ disputeId: dispute.id, decision, ...(parsed.data ? { note: parsed.data } : {}) }))
    } catch {
      // Shown from send.error in the dialog.
    }
  }

  return (
    <ConfirmDialog
      open
      title={`Update ${dispute.disputeNumber}`}
      confirmLabel="Send decision"
      busy={send.isPending}
      onConfirm={() => void onConfirm()}
      onCancel={onCancel}
    >
      <p>
        Currently <span className="font-medium">{statusLabel[dispute.status]}</span>.
      </p>
      <label htmlFor="decision" className="label mt-4">
        Decision
      </label>
      <select id="decision" className="input" value={decision} onChange={(e) => setDecision(e.target.value as DisputeDecision)}>
        {DECISIONS.map((d) => (
          <option key={d} value={d}>
            {decisionLabel[d]}
          </option>
        ))}
      </select>
      <OutcomeHint status={dispute.status} decision={decision} />
      <label htmlFor="decision-note" className="label mt-4">
        Note for the customer (optional)
      </label>
      <textarea
        id="decision-note"
        rows={3}
        maxLength={DECISION_NOTE_MAX}
        className="input"
        value={note}
        onChange={(e) => setNote(e.target.value)}
        aria-invalid={noteError ? true : undefined}
        aria-describedby={noteError ? 'decision-note-error' : undefined}
      />
      <FieldError id="decision-note-error" message={noteError ?? undefined} />
      {send.isError && (
        <div className="mt-4">
          <ErrorBanner message={send.error.message} />
        </div>
      )}
    </ConfirmDialog>
  )
}

function OutcomeHint({ status, decision }: { status: DisputeStatus; decision: DisputeDecision }) {
  const outcome = decisionOutcome(status, decision)
  if (outcome === 'applies') {
    return <p className="mt-2 text-sm text-slate-600">The consumer will apply this and add it to the customer's timeline.</p>
  }
  return (
    <p role="alert" className="mt-2 rounded-md border border-amber-200 bg-amber-50 p-3 text-sm text-amber-900">
      {outcome === 'stale'
        ? `This will not change anything: the dispute is already ${statusLabel[status]}, so the consumer will skip it as stale.`
        : `This will fail: it isn't allowed while the dispute is ${statusLabel[status]}, so the consumer will send it to dispute-decisions-dlq. Send Under review first, then Refund issued.`}
    </p>
  )
}
