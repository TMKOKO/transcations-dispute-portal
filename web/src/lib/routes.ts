import { useLocation } from 'react-router'
import type { Role } from '../api/types'

// Ids travel in router state, never in the address: the same rule as the API's POST lookups. Router
// state lives in the tab's history entry, so it survives a refresh and back/forward, but stays out
// of the address bar, bookmarks, shared links and the web server's access log. A page opened
// without it (a bookmark, a new tab, an old /disputes/{id} link) falls back to its list.

export const paths = {
  transactions: '/transactions',
  raiseDispute: '/transactions/dispute',
  disputes: '/disputes',
  disputeDetail: '/disputes/detail',
  agentDisputes: '/agent/disputes',
} as const

/** Where each role lands, and where it is sent from a page that is not for it. */
export const homeFor = (role: Role | undefined) => (role === 'agent' ? paths.agentDisputes : paths.transactions)

interface IdState {
  transactionId?: string
  disputeId?: string
}

/** `<Link {...raiseDisputeLink(id)}>` or `navigate(...)` props for the raise-dispute page. */
export const raiseDisputeLink = (transactionId: string) => ({ to: paths.raiseDispute, state: { transactionId } })

/** `<Link {...disputeLink(id)}>` props for the dispute detail page. */
export const disputeLink = (disputeId: string) => ({ to: paths.disputeDetail, state: { disputeId } })

/** The id this page was opened with, or null when it was opened without one. */
export function useStateId(key: keyof IdState): string | null {
  const state = useLocation().state as IdState | null
  const value = state?.[key]
  return typeof value === 'string' && value.length > 0 ? value : null
}
