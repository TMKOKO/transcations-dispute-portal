import { fireEvent, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import type { FormEvent } from 'react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { DateField } from './DateField'

function setup(props: { value?: string; min?: string; max?: string } = {}) {
  const onChange = vi.fn()
  const onSubmit = vi.fn((e: FormEvent) => e.preventDefault())
  const utils = render(
    <form onSubmit={onSubmit}>
      <label htmlFor="when">When</label>
      <DateField id="when" value="" onChange={onChange} {...props} />
    </form>,
  )
  return { onChange, onSubmit, user: userEvent.setup(), input: screen.getByLabelText('When'), ...utils }
}

describe('DateField', () => {
  afterEach(() => {
    delete (HTMLInputElement.prototype as { showPicker?: unknown }).showPicker
  })

  it('shows the value day first', () => {
    const { input } = setup({ value: '2026-02-13' })
    expect(input).toHaveValue('13/02/2026')
  })

  it('reports a typed date as yyyy-MM-dd on leaving the field, and tidies what was typed', async () => {
    const { input, onChange, user } = setup()

    await user.type(input, '5/1/2026')
    await user.tab()

    expect(onChange).toHaveBeenCalledExactlyOnceWith('2026-01-05')
    expect(input).toHaveValue('05/01/2026')
    expect(input).not.toHaveAttribute('aria-invalid')
  })

  it('commits on Enter without submitting the form around it', async () => {
    const { input, onChange, onSubmit, user } = setup()

    await user.type(input, '05/01/2026{Enter}')

    expect(onChange).toHaveBeenCalledExactlyOnceWith('2026-01-05')
    expect(onSubmit).not.toHaveBeenCalled()
  })

  it('reports nothing while the date is still being typed', async () => {
    const { input, onChange, user } = setup()

    await user.type(input, '05/01/2026')

    expect(onChange).not.toHaveBeenCalled()
  })

  it.each([
    ['not a date', {}, 'Enter a date as dd/mm/yyyy.'],
    ['31/02/2026', {}, 'Enter a date as dd/mm/yyyy.'],
    ['01/01/2026', { min: '2026-02-01' }, 'Choose a date on or after 01/02/2026.'],
    ['01/03/2026', { max: '2026-02-01' }, 'Choose a date on or before 01/02/2026.'],
  ])('refuses %s %o', async (text, limits, message) => {
    const { input, onChange, user } = setup(limits)

    await user.type(input, text)
    await user.tab()

    expect(input).toHaveAttribute('aria-invalid', 'true')
    expect(input).toHaveAccessibleDescription(message)
    expect(onChange).not.toHaveBeenCalled()
  })

  it('accepts the min and max dates themselves', async () => {
    const { input, onChange, user } = setup({ min: '2026-02-01', max: '2026-02-01' })

    await user.type(input, '01/02/2026')
    await user.tab()

    expect(onChange).toHaveBeenCalledExactlyOnceWith('2026-02-01')
  })

  it('clears the date, and the error, when emptied', async () => {
    const { input, onChange, user } = setup({ value: '2026-02-13' })

    await user.clear(input)
    await user.tab()

    expect(onChange).toHaveBeenCalledExactlyOnceWith('')
    expect(input).not.toHaveAttribute('aria-invalid')
  })

  it('does not report a date that has not changed', async () => {
    const { input, onChange, user } = setup({ value: '2026-02-13' })

    await user.click(input)
    await user.tab()

    expect(onChange).not.toHaveBeenCalled()
  })

  it('does not report an empty field that was already empty', async () => {
    const { input, onChange, user } = setup()

    await user.click(input)
    await user.tab()

    expect(onChange).not.toHaveBeenCalled()
  })

  it('follows a value changed elsewhere, dropping any error', async () => {
    const { input, user, rerender } = setup()
    await user.type(input, 'nope')
    await user.tab()
    expect(input).toHaveAttribute('aria-invalid', 'true')

    rerender(
      <form>
        <label htmlFor="when">When</label>
        <DateField id="when" value="2026-02-13" onChange={() => {}} />
      </form>,
    )

    expect(input).toHaveValue('13/02/2026')
    expect(input).not.toHaveAttribute('aria-invalid')
  })

  it('has no calendar button where the browser cannot open a picker', () => {
    setup()
    expect(screen.queryByRole('button', { name: 'Choose date' })).not.toBeInTheDocument()
  })

  it('opens the browser calendar and takes its date', async () => {
    const showPicker = vi.fn()
    HTMLInputElement.prototype.showPicker = showPicker
    const { container, onChange, user } = setup({ min: '2026-01-01', max: '2026-12-31' })

    await user.click(screen.getByRole('button', { name: 'Choose date' }))
    expect(showPicker).toHaveBeenCalledOnce()

    const picker = container.querySelector('input[type="date"]')!
    expect(picker).toHaveAttribute('min', '2026-01-01')
    expect(picker).toHaveAttribute('max', '2026-12-31')
    fireEvent.change(picker, { target: { value: '2026-03-04' } })
    expect(onChange).toHaveBeenCalledExactlyOnceWith('2026-03-04')
  })
})
