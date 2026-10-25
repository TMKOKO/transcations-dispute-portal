import { useState, type ReactNode } from 'react'
import { Link, useParams } from 'react-router'
import { ApiError } from '../api/client'
import { useDispute, useWithdrawDispute } from '../api/queries'
import type { DisputeDetail } from '../api/types'
import { ConfirmDialog } from '../components/ConfirmDialog'
import { Timeline } from '../components/Timeline'
import { Card, ErrorBanner, Spinner, StatusBadge } from '../components/ui'
import { formatDate, formatDateTime, formatMoney } from '../lib/format'
import { reasonLabel, statusExplainer } from '../lib/labels'
import { useDocumentTitle } from '../lib/useDocumentTitle'

export function DisputeDetailPage() {
  const { id = '' } = useParams()
  const query = useDispute(id)
  useDocumentTitle(query.data ? `Dispute ${query.data.disputeNumber}` : 'Dispute')

  if (query.isPending) return <Spinner label="Loading dispute" />
  if (query.isError) {
    return (
      <>
        <BackLink />
        <ErrorBanner
          message={query.error instanceof ApiError && query.error.status === 404 ? 'We could not find that dispute.' : query.error.message}
          onRetry={() => void query.refetch()}
        />
      </>
    )
  }
  return <Detail dispute={query.data} />
}

function BackLink() {
  return (
    <Link to="/disputes" className="mb-4 inline-block text-sm text-brand-700 hover:underline">
      ← All disputes
    </Link>
  )
}

function Detail({ dispute: d }: { dispute: DisputeDetail }) {
  const withdraw = useWithdrawDispute(d.id)
  const [confirming, setConfirming] = useState(false)
  const [note, setNote] = useState('')

  async function onWithdraw() {
    try {
      await withdraw.mutateAsync(note.trim() || undefined)
      setConfirming(false)
      setNote('')
    } catch {
      // The reason (e.g. an agent closed it meanwhile) is shown from withdraw.error on the page.
      setConfirming(false)
    }
  }

  return (
    <>
      <BackLink />
      <div className="mb-6 flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
        <div>
          <div className="flex flex-wrap items-center gap-3">
            <h1 className="text-2xl font-semibold tracking-tight text-slate-900">{d.disputeNumber}</h1>
            <StatusBadge status={d.status} />
          </div>
          <p className="mt-1 text-sm text-slate-600">{statusExplainer[d.status]}</p>
        </div>
        {d.canWithdraw && (
          <button type="button" className="btn-secondary" onClick={() => setConfirming(true)}>
            Withdraw dispute
          </button>
        )}
      </div>

      {withdraw.isError && (
        <div className="mb-4">
          <ErrorBanner message={withdraw.error.message} />
        </div>
      )}
      {withdraw.isSuccess && (
        <p role="status" className="mb-4 rounded-lg bg-slate-100 px-4 py-3 text-sm text-slate-700">
          Your dispute has been withdrawn.
        </p>
      )}

      <div className="grid gap-6 lg:grid-cols-[1fr_20rem]">
        <Card className="p-6">
          <h2 className="text-sm font-semibold text-slate-900">Details</h2>
          <dl className="mt-4 grid gap-4 text-sm sm:grid-cols-2">
            <Item label="Merchant">{d.transaction.merchantName}</Item>
            <Item label="Transaction date">{formatDate(d.transaction.transactionDate)}</Item>
            <Item label="Transaction amount">{formatMoney(d.transaction.amount, d.transaction.currency)}</Item>
            <Item label="Amount disputed">{formatMoney(d.disputedAmount, d.transaction.currency)}</Item>
            <Item label="Reason">{reasonLabel[d.reason]}</Item>
            <Item label="Raised">{formatDateTime(d.createdAt)}</Item>
            <div className="sm:col-span-2">
              <dt className="text-slate-500">What happened</dt>
              <dd className="mt-0.5 whitespace-pre-wrap text-slate-900">{d.description}</dd>
            </div>
            <Item label="Transaction reference">
              <span className="font-mono text-xs">{d.transaction.reference}</span>
            </Item>
          </dl>
        </Card>

        <Card className="p-6">
          <h2 className="mb-4 text-sm font-semibold text-slate-900">Progress</h2>
          <Timeline entries={d.timeline} />
        </Card>
      </div>

      <ConfirmDialog
        open={confirming}
        title="Withdraw this dispute?"
        confirmLabel="Withdraw dispute"
        cancelLabel="Keep dispute"
        tone="danger"
        busy={withdraw.isPending}
        onConfirm={() => void onWithdraw()}
        onCancel={() => setConfirming(false)}
      >
        <p>We'll stop working on {d.disputeNumber}. You can raise a new dispute on this transaction later if you need to.</p>
        <label htmlFor="withdraw-note" className="label mt-4">
          Reason (optional)
        </label>
        <textarea
          id="withdraw-note"
          rows={3}
          maxLength={500}
          className="input"
          value={note}
          onChange={(e) => setNote(e.target.value)}
          placeholder="For example: the merchant refunded me directly."
        />
      </ConfirmDialog>
    </>
  )
}

function Item({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div>
      <dt className="text-slate-500">{label}</dt>
      <dd className="mt-0.5 text-slate-900">{children}</dd>
    </div>
  )
}
