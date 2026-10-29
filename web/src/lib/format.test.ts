import { describe, expect, it } from 'vitest'
import { dmyToIso, isoToDmy } from './format'

describe('isoToDmy', () => {
  it('shows day first', () => {
    expect(isoToDmy('2026-10-05')).toBe('05/10/2026')
  })

  it('is empty for no date or anything else', () => {
    expect(isoToDmy('')).toBe('')
    expect(isoToDmy('05/10/2026')).toBe('')
  })
})

describe('dmyToIso', () => {
  it('reads day first', () => {
    expect(dmyToIso('05/10/2026')).toBe('2026-10-05')
    expect(dmyToIso('13/01/2026')).toBe('2026-01-13')
  })

  it('accepts single digits, other separators and surrounding spaces', () => {
    expect(dmyToIso('5/1/2026')).toBe('2026-01-05')
    expect(dmyToIso('05-10-2026')).toBe('2026-10-05')
    expect(dmyToIso(' 05.10.2026 ')).toBe('2026-10-05')
  })

  it('round-trips', () => {
    expect(dmyToIso(isoToDmy('2024-02-29'))).toBe('2024-02-29')
  })

  it('rejects dates that do not exist', () => {
    expect(dmyToIso('31/02/2026')).toBeNull()
    expect(dmyToIso('29/02/2026')).toBeNull()
    expect(dmyToIso('10/13/2026')).toBeNull()
    expect(dmyToIso('00/10/2026')).toBeNull()
  })

  it('rejects other shapes', () => {
    expect(dmyToIso('2026-10-05')).toBeNull()
    expect(dmyToIso('05/10/26')).toBeNull()
    expect(dmyToIso('yesterday')).toBeNull()
    expect(dmyToIso('')).toBeNull()
  })
})
