import type { TimelineEntry } from '../api/types'
import { formatDateTime } from '../lib/format'
import { actorLabel, statusLabel } from '../lib/labels'

const dot: Record<TimelineEntry['toStatus'], string> = {
  Submitted: 'bg-sky-500',
  UnderReview: 'bg-amber-500',
  Resolved: 'bg-emerald-500',
  Rejected: 'bg-rose-500',
  Withdrawn: 'bg-slate-400',
}

export function Timeline({ entries }: { entries: TimelineEntry[] }) {
  return (
    <ol aria-label="Dispute timeline" className="relative space-y-6 border-l border-slate-200 pl-6">
      {entries.map((entry, i) => (
        <li key={`${entry.changedAt}-${i}`} className="relative">
          <span
            aria-hidden
            className={`absolute -left-[1.84rem] top-1 size-3 rounded-full ring-4 ring-white ${dot[entry.toStatus]}`}
          />
          <p className="text-sm font-medium text-slate-900">
            {entry.fromStatus === null ? 'Dispute raised' : statusLabel[entry.toStatus]}
          </p>
          <p className="text-xs text-slate-500">
            <time dateTime={entry.changedAt}>{formatDateTime(entry.changedAt)}</time> · {actorLabel[entry.changedBy]}
          </p>
          {entry.note && <p className="mt-1.5 rounded-md bg-slate-50 px-3 py-2 text-sm text-slate-700">{entry.note}</p>}
        </li>
      ))}
    </ol>
  )
}
