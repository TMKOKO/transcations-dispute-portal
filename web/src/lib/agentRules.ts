import { z } from 'zod'
import type { DisputeDecision, DisputeStatus } from '../api/types'
import { isPlainText, PLAIN_TEXT_MESSAGE } from './disputeRules'

// Mirrors AgentDecisionRequest in src/DisputePortal.Api/DTOs/AgentDtos.cs. Change both together.
export const DECISION_NOTE_MAX = 500

export const decisionNoteSchema = z
  .string()
  .trim()
  .refine((s) => s.length <= DECISION_NOTE_MAX, `Keep it under ${DECISION_NOTE_MAX} characters.`)
  .refine((s) => isPlainText(s, true), PLAIN_TEXT_MESSAGE)

export const decisionLabel: Record<DisputeDecision, string> = {
  UnderReview: 'Under review',
  RefundIssued: 'Refund issued',
  Rejected: 'Rejected',
}

/** The status each decision moves a dispute to. Mirrors DisputeTopics.DecisionFor. */
const decisionTarget: Record<DisputeDecision, DisputeStatus> = {
  UnderReview: 'UnderReview',
  RefundIssued: 'Resolved',
  Rejected: 'Rejected',
}

/** Mirrors Dispute.AllowedTransitions, for the moves a decision can make. */
const allowed: Record<DisputeStatus, DisputeStatus[]> = {
  Submitted: ['UnderReview', 'Rejected'],
  UnderReview: ['Resolved', 'Rejected'],
  Resolved: [],
  Rejected: [],
  Withdrawn: [],
}

export type DecisionOutcome = 'applies' | 'stale' | 'dead-letter'

/**
 * What the consumer will do with this decision, mirroring DecisionHandler. Only a warning: the API
 * sends it either way, and the consumer has the last word.
 */
export function decisionOutcome(status: DisputeStatus, decision: DisputeDecision): DecisionOutcome {
  const target = decisionTarget[decision]
  if (allowed[status].includes(target)) return 'applies'
  return allowed[status].length === 0 || status === target ? 'stale' : 'dead-letter'
}
