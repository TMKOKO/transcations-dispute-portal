import { http, HttpResponse } from 'msw'
import { setupServer } from 'msw/node'
import { API_BASE } from '../api/client'
import type { DisputeDetail, DisputeReasonInfo, Paged, Transaction } from '../api/types'

export const api = (path: string) => `${API_BASE}${path}`

export const eligibleTx: Transaction = {
  id: 'tx-eligible',
  reference: 'TXN-20261015-AAAAAA',
  merchantName: 'Computicket',
  merchantCategory: 'Entertainment',
  amount: 850,
  currency: 'ZAR',
  type: 'Debit',
  channel: 'Card',
  status: 'Settled',
  cardLast4: '4821',
  transactionDate: '2026-10-15T15:00:00Z',
  canDispute: true,
  ineligibleCode: null,
  ineligibleMessage: null,
  latestDispute: null,
}

export const openDisputeTx: Transaction = {
  ...eligibleTx,
  id: 'tx-open',
  reference: 'TXN-20261001-BBBBBB',
  merchantName: 'Takealot.com',
  canDispute: false,
  ineligibleCode: 'ActiveDispute',
  ineligibleMessage: 'This transaction already has an open dispute.',
  latestDispute: { id: 'd-1', disputeNumber: 'DSP-2026-001000', status: 'UnderReview' },
}

export const oldTx: Transaction = {
  ...eligibleTx,
  id: 'tx-old',
  reference: 'TXN-20260501-CCCCCC',
  merchantName: 'Engen Garage',
  canDispute: false,
  ineligibleCode: 'OutsideWindow',
  ineligibleMessage: 'Transactions can only be disputed within 120 days.',
}

export function paged<T>(items: T[]): Paged<T> {
  return { items, page: 1, pageSize: 15, totalCount: items.length, totalPages: items.length ? 1 : 0 }
}

export const reasons: DisputeReasonInfo[] = [
  { code: 'Unauthorised', label: "I didn't make this transaction", description: '' },
  { code: 'DuplicateCharge', label: 'I was charged more than once', description: '' },
  { code: 'Other', label: 'Something else', description: '' },
]

export const disputeDetail: DisputeDetail = {
  id: 'd-1',
  disputeNumber: 'DSP-2026-001000',
  status: 'UnderReview',
  reason: 'DuplicateCharge',
  description: 'This payment looks like it was taken twice for one purchase.',
  disputedAmount: 850,
  createdAt: '2026-10-02T08:00:00Z',
  updatedAt: '2026-10-03T08:00:00Z',
  resolvedAt: null,
  canWithdraw: true,
  transaction: {
    id: 'tx-open',
    reference: 'TXN-20261001-BBBBBB',
    merchantName: 'Takealot.com',
    amount: 850,
    currency: 'ZAR',
    transactionDate: '2026-10-01T10:00:00Z',
  },
  timeline: [
    { fromStatus: null, toStatus: 'Submitted', note: null, changedBy: 'customer', changedAt: '2026-10-02T08:00:00Z' },
    {
      fromStatus: 'Submitted',
      toStatus: 'UnderReview',
      note: 'Your case has been assigned to a disputes specialist.',
      changedBy: 'agent',
      changedAt: '2026-10-03T08:00:00Z',
    },
  ],
}

/** Defaults every test starts from; individual tests override with server.use(...). */
export const handlers = [
  http.get(api('/api/transactions'), () => HttpResponse.json(paged([eligibleTx, openDisputeTx, oldTx]))),
  http.post(api('/api/transactions/lookup'), async ({ request }) => {
    const { transactionId } = (await request.json()) as { transactionId: string }
    const tx = [eligibleTx, openDisputeTx, oldTx].find((t) => t.id === transactionId)
    return tx
      ? HttpResponse.json(tx)
      : HttpResponse.json({ status: 404, detail: 'Transaction not found.', code: 'transaction_not_found' }, { status: 404 })
  }),
  http.get(api('/api/dispute-reasons'), () => HttpResponse.json(reasons)),
  http.get(api('/api/disputes'), () => HttpResponse.json(paged([disputeDetail]))),
  http.post(api('/api/disputes/lookup'), () => HttpResponse.json(disputeDetail)),
  http.post(api('/api/auth/logout'), () => new HttpResponse(null, { status: 204 })),
]

export const server = setupServer(...handlers)
