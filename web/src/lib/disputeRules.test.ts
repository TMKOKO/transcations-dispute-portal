import { describe, expect, it } from 'vitest'
import { isPlainText, withdrawNoteSchema } from './disputeRules'
import { loginSchema } from './loginRules'

describe('isPlainText', () => {
  it('rejects control characters and bidi overrides, like PlainTextAttribute', () => {
    expect(isPlainText('a\u0000b')).toBe(false)
    expect(isPlainText('a\u001b[31mb')).toBe(false)
    expect(isPlainText('a‮b')).toBe(false)
    expect(isPlainText('a⁦b')).toBe(false)
    expect(isPlainText('Café — R 1 200,00')).toBe(true)
  })

  it('allows line breaks and tabs only when asked', () => {
    expect(isPlainText('a\nb\tc\r\n')).toBe(false)
    expect(isPlainText('a\nb\tc\r\n', true)).toBe(true)
  })
})

describe('withdrawNoteSchema', () => {
  it.each([
    ['', true],
    ['   ', true],
    ['ok', false],
    ['Refunded', true],
    ['Paid back.\nThanks', true],
    ['a'.repeat(501), false],
    ['Refunded\u0000', false],
  ])('%j valid: %s', (note, valid) => {
    expect(withdrawNoteSchema.safeParse(note).success).toBe(valid)
  })
})

describe('loginSchema', () => {
  it('trims the email and keeps the password as typed', () => {
    expect(loginSchema.parse({ email: ' thandi@example.com ', password: ' pw ' })).toEqual({
      email: 'thandi@example.com',
      password: ' pw ',
    })
  })

  it.each([
    [{ email: '', password: 'x' }, 'email'],
    [{ email: 'thandi', password: 'x' }, 'email'],
    [{ email: `${'a'.repeat(243)}@example.com`, password: 'x' }, 'email'],
    [{ email: 'thandi@example.com', password: '   ' }, 'password'],
    [{ email: 'thandi@example.com', password: 'x'.repeat(201) }, 'password'],
  ])('rejects %j on %s', (values, field) => {
    const result = loginSchema.safeParse(values)
    expect(result.success).toBe(false)
    expect(result.error?.issues[0]?.path[0]).toBe(field)
  })
})
