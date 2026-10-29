import type { ReactNode } from 'react'
import type { DisputeStatus } from '../api/types'
import { formatMoney } from '../lib/format'
import { statusLabel } from '../lib/labels'

const statusStyles: Record<DisputeStatus, string> = {
  Submitted: 'bg-sky-50 text-sky-800 ring-sky-600/20',
  UnderReview: 'bg-amber-50 text-amber-800 ring-amber-600/25',
  Resolved: 'bg-emerald-50 text-emerald-800 ring-emerald-600/20',
  Rejected: 'bg-rose-50 text-rose-800 ring-rose-600/20',
  Withdrawn: 'bg-slate-100 text-slate-700 ring-slate-500/20',
}

export function StatusBadge({ status }: { status: DisputeStatus }) {
  return (
    <span
      className={`inline-flex items-center rounded-full px-2.5 py-0.5 text-xs font-medium ring-1 ring-inset ${statusStyles[status]}`}
    >
      {statusLabel[status]}
    </span>
  )
}

export function Money({ amount, currency, negative }: { amount: number; currency?: string; negative?: boolean }) {
  return (
    <span className={`tabular-nums ${negative ? 'text-slate-900' : 'text-emerald-700'}`}>
      {negative ? '−' : '+'}
      {formatMoney(amount, currency)}
    </span>
  )
}

export function Spinner({ label = 'Loading' }: { label?: string }) {
  return (
    <div role="status" className="flex items-center justify-center gap-3 py-16 text-slate-500">
      <span className="size-5 animate-spin rounded-full border-2 border-slate-300 border-t-brand-600" aria-hidden />
      <span>{label}…</span>
    </div>
  )
}

export function ErrorBanner({ message, onRetry }: { message: string; onRetry?: () => void }) {
  return (
    <div role="alert" className="flex items-start justify-between gap-4 rounded-lg border border-rose-200 bg-rose-50 p-4 text-sm text-rose-800">
      <p>{message}</p>
      {onRetry && (
        <button type="button" onClick={onRetry} className="shrink-0 font-medium underline underline-offset-2 hover:text-rose-950">
          Try again
        </button>
      )}
    </div>
  )
}

/** The message under a form field; point the field's aria-describedby at the same id. */
export function FieldError({ id, message }: { id: string; message?: string }) {
  if (!message) return null
  return (
    <p id={id} className="mt-1.5 text-sm text-rose-700">
      {message}
    </p>
  )
}

export function EmptyState({ title, children }: { title: string; children?: ReactNode }) {
  return (
    <div className="rounded-xl border border-dashed border-slate-300 bg-white px-6 py-14 text-center">
      <p className="font-medium text-slate-900">{title}</p>
      {children && <div className="mt-1 text-sm text-slate-500">{children}</div>}
    </div>
  )
}

export function Card({ children, className = '' }: { children: ReactNode; className?: string }) {
  return <section className={`rounded-xl border border-slate-200 bg-white shadow-sm ${className}`}>{children}</section>
}

export function PageHeader({ title, description, action }: { title: string; description?: ReactNode; action?: ReactNode }) {
  return (
    <div className="mb-6 flex flex-col gap-3 sm:flex-row sm:items-end sm:justify-between">
      <div>
        <h1 className="text-2xl font-semibold tracking-tight text-slate-900">{title}</h1>
        {description && <p className="mt-1 text-sm text-slate-600">{description}</p>}
      </div>
      {action}
    </div>
  )
}

export function Pagination({
  page,
  totalPages,
  totalCount,
  onChange,
}: {
  page: number
  totalPages: number
  totalCount: number
  onChange: (page: number) => void
}) {
  if (totalPages <= 1) return null
  return (
    <nav aria-label="Pagination" className="flex items-center justify-between gap-4 border-t border-slate-200 px-4 py-3 text-sm">
      <p className="text-slate-600">
        Page <span className="font-medium text-slate-900">{page}</span> of {totalPages} · {totalCount} results
      </p>
      <div className="flex gap-2">
        <button type="button" className="btn-secondary" disabled={page <= 1} onClick={() => onChange(page - 1)}>
          Previous
        </button>
        <button type="button" className="btn-secondary" disabled={page >= totalPages} onClick={() => onChange(page + 1)}>
          Next
        </button>
      </div>
    </nav>
  )
}
