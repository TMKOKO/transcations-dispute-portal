import { describe, expect, it } from 'vitest'
import type { DisputeDecision, DisputeStatus } from '../api/types'
import { decisionNoteSchema, decisionOutcome } from './agentRules'

describe('decisionOutcome', () => {
  it.each<[DisputeStatus, DisputeDecision, string]>([
    ['Submitted', 'UnderReview', 'applies'],
    ['Submitted', 'Rejected', 'applies'],
    ['Submitted', 'RefundIssued', 'dead-letter'],
    ['UnderReview', 'RefundIssued', 'applies'],
    ['UnderReview', 'Rejected', 'applies'],
    ['UnderReview', 'UnderReview', 'stale'],
    ['Resolved', 'Rejected', 'stale'],
    ['Resolved', 'RefundIssued', 'stale'],
    ['Rejected', 'UnderReview', 'stale'],
    ['Withdrawn', 'RefundIssued', 'stale'],
  ])('%s + %s: %s', (status, decision, outcome) => {
    expect(decisionOutcome(status, decision)).toBe(outcome)
  })
})

describe('decisionNoteSchema', () => {
  it.each([
    ['', true],
    ['   ', true],
    ['ok', true],
    ['Refund issued.\nThanks', true],
    ['a'.repeat(501), false],
    ['Refund\u0000', false],
  ])('%j valid: %s', (note, valid) => {
    expect(decisionNoteSchema.safeParse(note).success).toBe(valid)
  })
})
