import { useEffect, useState } from 'react'
import { Link, useSearchParams } from 'react-router'
import { useTransactions } from '../api/queries'
import type { Transaction } from '../api/types'
import { EmptyState, ErrorBanner, Money, PageHeader, Pagination, Spinner, StatusBadge, Card } from '../components/ui'
import { formatDate, formatMoney, todaySast } from '../lib/format'
import { channelLabel } from '../lib/labels'
import { disputeLink, raiseDisputeLink } from '../lib/routes'
import { useDocumentTitle } from '../lib/useDocumentTitle'

const PAGE_SIZE = 15

export function TransactionsPage() {
  useDocumentTitle('Transactions')

  // Filters live in the URL, so they survive a refresh and the back button after raising a dispute.
  const [params, setParams] = useSearchParams()
  const search = params.get('search') ?? ''
  const from = params.get('from') ?? ''
  const to = params.get('to') ?? ''
  const page = Math.max(1, Number(params.get('page')) || 1)

  const [searchInput, setSearchInput] = useState(search)

  function update(next: Record<string, string>) {
    const merged = new URLSearchParams(params)
    for (const [key, value] of Object.entries(next)) {
      if (value) merged.set(key, value)
      else merged.delete(key)
    }
    // Any filter change starts again from page 1.
    if (!('page' in next)) merged.delete('page')
    setParams(merged, { replace: true })
  }

  // Debounce typing so each keystroke is not a request.
  useEffect(() => {
    if (searchInput === search) return
    const t = window.setTimeout(() => update({ search: searchInput.trim() }), 300)
    return () => window.clearTimeout(t)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [searchInput])

  const dateError = from && to && from > to ? 'The start date must be on or before the end date.' : null
  const query = useTransactions({
    search: search || undefined,
    from: dateError ? undefined : from || undefined,
    to: dateError ? undefined : to || undefined,
    page,
    pageSize: PAGE_SIZE,
  })

  const hasFilters = Boolean(search || from || to)

  return (
    <>
      <PageHeader
        title="Transactions"
        description="Select a transaction to dispute it. Card and debit order payments can be disputed within 120 days."
      />

      <Card className="mb-4 p-4">
        <form role="search" onSubmit={(e) => e.preventDefault()} className="grid gap-3 sm:grid-cols-[1fr_auto_auto_auto] sm:items-end">
          <div>
            <label htmlFor="search" className="label">
              Search
            </label>
            <input
              id="search"
              type="search"
              className="input"
              placeholder="Merchant or reference"
              value={searchInput}
              onChange={(e) => setSearchInput(e.target.value)}
            />
          </div>
          <div>
            <label htmlFor="from" className="label">
              From
            </label>
            <input id="from" type="date" className="input" max={to || todaySast()} value={from} onChange={(e) => update({ from: e.target.value })} />
          </div>
          <div>
            <label htmlFor="to" className="label">
              To
            </label>
            <input id="to" type="date" className="input" min={from || undefined} max={todaySast()} value={to} onChange={(e) => update({ to: e.target.value })} />
          </div>
          <button
            type="button"
            className="btn-secondary"
            disabled={!hasFilters}
            onClick={() => {
              setSearchInput('')
              setParams(new URLSearchParams(), { replace: true })
            }}
          >
            Clear
          </button>
        </form>
        {dateError && (
          <p role="alert" className="mt-2 text-sm text-rose-700">
            {dateError}
          </p>
        )}
      </Card>

      {query.isPending ? (
        <Spinner label="Loading transactions" />
      ) : query.isError ? (
        <ErrorBanner message={query.error.message} onRetry={() => void query.refetch()} />
      ) : query.data.items.length === 0 ? (
        <EmptyState title={hasFilters ? 'No transactions match your filters' : 'No transactions yet'}>
          {hasFilters && 'Try a different search or date range.'}
        </EmptyState>
      ) : (
        <Card className={query.isPlaceholderData ? 'opacity-60 transition-opacity' : ''}>
          <ul aria-label="Transactions" aria-busy={query.isFetching} className="divide-y divide-slate-100">
            {query.data.items.map((t) => (
              <TransactionRow key={t.id} transaction={t} />
            ))}
          </ul>
          <Pagination
            page={query.data.page}
            totalPages={query.data.totalPages}
            totalCount={query.data.totalCount}
            onChange={(p) => update({ page: String(p) })}
          />
        </Card>
      )}
    </>
  )
}

function TransactionRow({ transaction: t }: { transaction: Transaction }) {
  const isDebit = t.type === 'Debit'
  const open = t.latestDispute && (t.latestDispute.status === 'Submitted' || t.latestDispute.status === 'UnderReview')

  return (
    <li className="grid grid-cols-[1fr_auto] gap-x-4 gap-y-2 px-4 py-4 sm:grid-cols-[7rem_1fr_auto_11rem] sm:items-center">
      <p className="order-3 text-xs text-slate-500 sm:order-none sm:text-sm">
        <time dateTime={t.transactionDate}>{formatDate(t.transactionDate)}</time>
      </p>

      <div className="min-w-0">
        <p className="truncate font-medium text-slate-900">
          {t.merchantName}
          {t.status === 'Pending' && <span className="ml-2 rounded bg-slate-100 px-1.5 py-0.5 text-xs font-normal text-slate-600">Pending</span>}
          {t.status === 'Reversed' && <span className="ml-2 rounded bg-slate-100 px-1.5 py-0.5 text-xs font-normal text-slate-600">Reversed</span>}
        </p>
        <p className="truncate text-xs text-slate-500">
          {t.merchantCategory} · {channelLabel[t.channel]}
          {t.cardLast4 && ` •••• ${t.cardLast4}`} · {t.reference}
        </p>
      </div>

      <p className={`text-right font-medium ${t.status === 'Reversed' ? 'line-through decoration-slate-400' : ''}`}>
        <Money amount={t.amount} currency={t.currency} negative={isDebit} />
      </p>

      <div className="order-4 col-span-2 sm:order-none sm:col-span-1 sm:text-right">
        {t.canDispute ? (
          <Link
            {...raiseDisputeLink(t.id)}
            className="btn-secondary w-full sm:w-auto"
            aria-label={`Dispute ${t.merchantName} payment of ${formatMoney(t.amount, t.currency)} on ${formatDate(t.transactionDate)}`}
          >
            Dispute
          </Link>
        ) : open && t.latestDispute ? (
          <Link {...disputeLink(t.latestDispute.id)} className="inline-flex items-center gap-2 text-sm text-brand-700 hover:underline">
            <StatusBadge status={t.latestDispute.status} />
            <span>View {t.latestDispute.disputeNumber}</span>
          </Link>
        ) : (
          <p className="text-xs text-slate-500">{t.ineligibleMessage}</p>
        )}
      </div>
    </li>
  )
}
