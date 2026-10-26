import { z } from 'zod'
import type { DisputeReason } from '../api/types'

// Mirrors RaiseDisputeRequest in src/DisputePortal.Api/DTOs/DisputeDtos.cs. Change both together:
// the API is the authority, and a looser rule here means the customer only finds out on submit.
export const DESCRIPTION_MIN = 20
export const DESCRIPTION_MAX = 1000

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
      .max(DESCRIPTION_MAX, `Keep it under ${DESCRIPTION_MAX} characters.`),
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
