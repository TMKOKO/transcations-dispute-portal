import type { DisputeReason, DisputeStatus, TransactionChannel } from '../api/types'

export const statusLabel: Record<DisputeStatus, string> = {
  Submitted: 'Submitted',
  UnderReview: 'Under review',
  Resolved: 'Resolved',
  Rejected: 'Declined',
  Withdrawn: 'Withdrawn',
}

/** What each status means for the customer, shown on the dispute page. */
export const statusExplainer: Record<DisputeStatus, string> = {
  Submitted: 'We have your dispute and will start reviewing it shortly.',
  UnderReview: 'A disputes specialist is looking into this with the merchant.',
  Resolved: 'This dispute was resolved in your favour.',
  Rejected: 'After review, we could not uphold this dispute. The reason is in the timeline below.',
  Withdrawn: 'You withdrew this dispute.',
}

// Fallback wording if /api/dispute-reasons has not loaded; the API is the source of truth.
export const reasonLabel: Record<DisputeReason, string> = {
  Unauthorised: "I didn't make this transaction",
  DuplicateCharge: 'I was charged more than once',
  IncorrectAmount: 'The amount is wrong',
  GoodsNotReceived: "I didn't receive what I paid for",
  CancelledSubscription: 'I cancelled this subscription',
  Other: 'Something else',
}

export const channelLabel: Record<TransactionChannel, string> = {
  Card: 'Card',
  Eft: 'EFT',
  DebitOrder: 'Debit order',
}

export const actorLabel = { customer: 'You', agent: 'Disputes team', system: 'System' } as const
