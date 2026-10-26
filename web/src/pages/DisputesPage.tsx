import { Link, useSearchParams } from 'react-router'
import { useDisputes } from '../api/queries'
import type { DisputeStatus } from '../api/types'
import { Card, EmptyState, ErrorBanner, PageHeader, Pagination, Spinner, StatusBadge } from '../components/ui'
import { formatDate, formatMoney } from '../lib/format'
import { reasonLabel, statusLabel } from '../lib/labels'
import { disputeLink } from '../lib/routes'
import { useDocumentTitle } from '../lib/useDocumentTitle'

const FILTERS: (DisputeStatus | undefined)[] = [undefined, 'Submitted', 'UnderReview', 'Resolved', 'Rejected', 'Withdrawn']

export function DisputesPage() {
  useDocumentTitle('My disputes')
  const [params, setParams] = useSearchParams()
  const status = (params.get('status') as DisputeStatus | null) ?? undefined
  const page = Math.max(1, Number(params.get('page')) || 1)
  const query = useDisputes(status, page)

  function setFilter(next: DisputeStatus | undefined) {
    setParams(next ? { status: next } : {}, { replace: true })
  }

  return (
    <>
      <PageHeader
        title="My disputes"
        description="Track the progress of disputes you've raised."
        action={
          <Link to="/transactions" className="btn-primary">
            Raise a new dispute
          </Link>
        }
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

      {query.isPending ? (
        <Spinner label="Loading disputes" />
      ) : query.isError ? (
        <ErrorBanner message={query.error.message} onRetry={() => void query.refetch()} />
      ) : query.data.items.length === 0 ? (
        <EmptyState title={status ? `No ${statusLabel[status].toLowerCase()} disputes` : "You haven't raised any disputes"}>
          {!status && (
            <>
              Find the payment on your <Link to="/transactions" className="text-brand-700 underline">transactions</Link> page and choose Dispute.
            </>
          )}
        </EmptyState>
      ) : (
        <Card>
          <ul aria-label="Disputes" className="divide-y divide-slate-100">
            {query.data.items.map((d) => (
              <li key={d.id}>
                <Link
                  {...disputeLink(d.id)}
                  className="grid grid-cols-[1fr_auto] items-center gap-x-4 gap-y-1 px-4 py-4 hover:bg-slate-50 focus-visible:bg-slate-50 sm:grid-cols-[9rem_1fr_auto_7rem]"
                >
                  <span className="font-mono text-sm text-slate-700">{d.disputeNumber}</span>
                  <span className="col-span-2 min-w-0 sm:col-span-1">
                    <span className="block truncate font-medium text-slate-900">{d.transaction.merchantName}</span>
                    <span className="block truncate text-xs text-slate-500">
                      {reasonLabel[d.reason]} · raised {formatDate(d.createdAt)}
                    </span>
                  </span>
                  <span className="text-right text-sm font-medium tabular-nums text-slate-900">
                    {formatMoney(d.disputedAmount, d.transaction.currency)}
                  </span>
                  <span className="row-start-1 text-right sm:row-start-auto">
                    <StatusBadge status={d.status} />
                  </span>
                </Link>
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
    </>
  )
}
