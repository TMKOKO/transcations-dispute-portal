import { z } from 'zod'
import type { DisputeReason } from '../api/types'

// Mirrors RaiseDisputeRequest in src/DisputePortal.Api/DTOs/DisputeDtos.cs. Change both together:
// the API is the authority, and a looser rule here means the customer only finds out on submit.
export const DESCRIPTION_MIN = 20
export const DESCRIPTION_MAX = 1000

// Mirrors WithdrawDisputeRequest: optional, but a note that is there must be 5-500 characters.
export const NOTE_MIN = 5
export const NOTE_MAX = 500

// Mirrors PlainTextAttribute (src/DisputePortal.Api/Helpers): control characters and bidi
// overrides, which make text display differently from what it says. Line breaks are opt-in.
// eslint-disable-next-line no-control-regex
const CONTROL = /[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F-\u009F‪-‮⁦-⁩]/
const LINE_BREAKS = /[\t\n\r]/

export function isPlainText(text: string, allowLineBreaks = false): boolean {
  return !CONTROL.test(text) && (allowLineBreaks || !LINE_BREAKS.test(text))
}

export const PLAIN_TEXT_MESSAGE = 'Remove any special or invisible characters, such as ones pasted from another app.'

export const withdrawNoteSchema = z
  .string()
  .trim()
  .refine((s) => s.length === 0 || s.length >= NOTE_MIN, `Tell us a little more — at least ${NOTE_MIN} characters, or leave it blank.`)
  .refine((s) => s.length <= NOTE_MAX, `Keep it under ${NOTE_MAX} characters.`)
  .refine((s) => isPlainText(s, true), PLAIN_TEXT_MESSAGE)

const reasons = [
  'Unauthorised',
  'DuplicateCharge',
  'IncorrectAmount',
  'GoodsNotReceived',
  'CancelledSubscription',
  'Other',
] as const satisfies readonly DisputeReason[]

/** Form values are strings (as inputs give them); the schema turns them into the request body. */
export function disputeFormSchema(transactionAmount: number) {
  return z.object({
    reason: z.enum(reasons, { error: 'Choose the reason that best describes the problem.' }),
    description: z
      .string()
      .trim()
      .min(DESCRIPTION_MIN, `Tell us a little more — at least ${DESCRIPTION_MIN} characters.`)
      .max(DESCRIPTION_MAX, `Keep it under ${DESCRIPTION_MAX} characters.`)
      .refine((s) => isPlainText(s, true), PLAIN_TEXT_MESSAGE),
    disputedAmount: z
      .string()
      .trim()
      .min(1, 'Enter the amount you are disputing.')
      .regex(/^\d+(\.\d{1,2})?$/, 'Enter an amount like 150 or 150.50.')
      .transform(Number)
      .refine((n) => n > 0, 'The amount must be more than zero.')
      .refine((n) => n <= transactionAmount, 'You cannot dispute more than the transaction amount.'),
  })
}

export type DisputeFormValues = { reason: string; description: string; disputedAmount: string }
export type DisputeFormErrors = Partial<Record<keyof DisputeFormValues, string>>

/** Unique per form instance, so a retried or double-clicked submit is recognised as the same one. */
export function newIdempotencyKey(): string {
  return `web-${crypto.randomUUID()}`
}
