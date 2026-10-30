// Mirrors the API's DTOs (src/DisputePortal.Api/DTOs). Enums travel as strings.

export type TransactionType = 'Debit' | 'Credit'
export type TransactionChannel = 'Card' | 'Eft' | 'DebitOrder'
export type TransactionStatus = 'Pending' | 'Settled' | 'Reversed'

export type DisputeStatus = 'Submitted' | 'UnderReview' | 'Resolved' | 'Rejected' | 'Withdrawn'

export type DisputeReason =
  | 'Unauthorised'
  | 'DuplicateCharge'
  | 'IncorrectAmount'
  | 'GoodsNotReceived'
  | 'CancelledSubscription'
  | 'Other'

export type IneligibleCode =
  | 'ActiveDispute'
  | 'AlreadyResolved'
  | 'NotADebit'
  | 'Reversed'
  | 'Pending'
  | 'OutsideWindow'

export interface Paged<T> {
  items: T[]
  page: number
  pageSize: number
  totalCount: number
  totalPages: number
}

/** No token: the session is an httpOnly cookie. */
export interface LoginResponse {
  displayName: string
  role: Role
  idleTimeoutSeconds: number
}

export type Role = 'customer' | 'agent'

export interface Me {
  id: string
  firstName: string
  lastName: string
  email: string
}

export interface DisputeRef {
  id: string
  disputeNumber: string
  status: DisputeStatus
}

export interface Transaction {
  id: string
  reference: string
  merchantName: string
  merchantCategory: string
  amount: number
  currency: string
  type: TransactionType
  channel: TransactionChannel
  status: TransactionStatus
  cardLast4: string | null
  transactionDate: string
  canDispute: boolean
  ineligibleCode: IneligibleCode | null
  ineligibleMessage: string | null
  latestDispute: DisputeRef | null
}

export interface TransactionBrief {
  id: string
  reference: string
  merchantName: string
  amount: number
  currency: string
  transactionDate: string
}

export interface DisputeSummary {
  id: string
  disputeNumber: string
  status: DisputeStatus
  reason: DisputeReason
  disputedAmount: number
  createdAt: string
  updatedAt: string
  transaction: TransactionBrief
}

export interface TimelineEntry {
  fromStatus: DisputeStatus | null
  toStatus: DisputeStatus
  note: string | null
  changedBy: 'customer' | 'agent' | 'system'
  changedAt: string
}

export interface DisputeDetail extends DisputeSummary {
  description: string
  resolvedAt: string | null
  canWithdraw: boolean
  timeline: TimelineEntry[]
}

export interface DisputeReasonInfo {
  code: DisputeReason
  label: string
  description: string
}

export type DisputeDecision = 'UnderReview' | 'RefundIssued' | 'Rejected'

export interface AgentDispute {
  id: string
  disputeNumber: string
  status: DisputeStatus
  reason: DisputeReason
  disputedAmount: number
  currency: string
  merchantName: string
  createdAt: string
  updatedAt: string
}

export interface AgentDecisionBody {
  disputeId: string
  decision: DisputeDecision
  note?: string
}

export interface AgentDecisionResult {
  eventId: string
  eventType: string
  topic: string
}

export interface RaiseDisputeBody {
  transactionId: string
  reason: DisputeReason
  description: string
  disputedAmount: number
}
