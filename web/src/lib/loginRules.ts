import { z } from 'zod'
import { isPlainText } from './disputeRules'

// Mirrors LoginRequest in src/DisputePortal.Api/DTOs/AuthDtos.cs. Change both together.
export const EMAIL_MAX = 254
export const PASSWORD_MAX = 200

export const loginSchema = z.object({
  email: z
    .string()
    .trim()
    .min(1, 'Enter your email address.')
    .max(EMAIL_MAX, 'Enter a valid email address, like name@example.com.')
    .refine((s) => isPlainText(s), 'Enter a valid email address, like name@example.com.')
    .pipe(z.email('Enter a valid email address, like name@example.com.')),
  // Not trimmed: spaces can be part of a password.
  password: z
    .string()
    .refine((s) => s.trim().length > 0, 'Enter your password.')
    .refine((s) => s.length <= PASSWORD_MAX, `Passwords are at most ${PASSWORD_MAX} characters.`),
})

export type LoginValues = z.input<typeof loginSchema>
export type LoginErrors = Partial<Record<keyof LoginValues, string>>
