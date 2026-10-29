import { useEffect, useRef, useState } from 'react'
import { dmyToIso, isoToDmy } from '../lib/format'

interface DateFieldProps {
  id: string
  /** yyyy-MM-dd, or '' for no date. */
  value: string
  min?: string
  max?: string
  onChange: (iso: string) => void
}

/**
 * A date typed and shown as dd/mm/yyyy. A native date input lays itself out in the browser's
 * language, so with Chrome in US English it reads mm/dd/yyyy whatever the page's lang says.
 * The value in and out stays yyyy-MM-dd, as the URL and the API expect.
 */
export function DateField({ id, value, min, max, onChange }: DateFieldProps) {
  const [draft, setDraft] = useState(() => isoToDmy(value))
  const [error, setError] = useState<string | null>(null)
  const picker = useRef<HTMLInputElement>(null)
  const errorId = `${id}-error`

  // Follow changes made elsewhere: Clear, the calendar, or the back button.
  useEffect(() => {
    setDraft(isoToDmy(value))
    setError(null)
  }, [value])

  function commit() {
    const text = draft.trim()
    if (!text) {
      setError(null)
      if (value) onChange('')
      return
    }
    const iso = dmyToIso(text)
    if (!iso) return setError('Enter a date as dd/mm/yyyy.')
    if (min && iso < min) return setError(`Choose a date on or after ${isoToDmy(min)}.`)
    if (max && iso > max) return setError(`Choose a date on or before ${isoToDmy(max)}.`)
    setError(null)
    setDraft(isoToDmy(iso))
    if (iso !== value) onChange(iso)
  }

  const canPick = typeof HTMLInputElement !== 'undefined' && 'showPicker' in HTMLInputElement.prototype

  return (
    <>
      <div className="relative">
        <input
          id={id}
          type="text"
          inputMode="numeric"
          autoComplete="off"
          placeholder="dd/mm/yyyy"
          className={canPick ? 'input pr-10' : 'input'}
          value={draft}
          aria-invalid={error ? true : undefined}
          aria-describedby={error ? errorId : undefined}
          onChange={(e) => setDraft(e.target.value)}
          onBlur={commit}
          onKeyDown={(e) => {
            if (e.key === 'Enter') {
              e.preventDefault()
              commit()
            }
          }}
        />
        {canPick && (
          <>
            <button
              type="button"
              aria-label="Choose date"
              className="absolute inset-y-0 right-0 mt-1 flex items-center px-3 text-slate-500 hover:text-slate-700"
              onClick={() => picker.current?.showPicker()}
            >
              <svg viewBox="0 0 20 20" fill="currentColor" className="size-4" aria-hidden="true">
                <path d="M5.75 2a.75.75 0 0 1 .75.75V4h7V2.75a.75.75 0 0 1 1.5 0V4h.25A2.75 2.75 0 0 1 18 6.75v8.5A2.75 2.75 0 0 1 15.25 18H4.75A2.75 2.75 0 0 1 2 15.25v-8.5A2.75 2.75 0 0 1 4.75 4H5V2.75A.75.75 0 0 1 5.75 2Zm-1 5.5c-.69 0-1.25.56-1.25 1.25v6.5c0 .69.56 1.25 1.25 1.25h10.5c.69 0 1.25-.56 1.25-1.25v-6.5c0-.69-.56-1.25-1.25-1.25H4.75Z" />
              </svg>
            </button>
            {/* Only there to open the browser's calendar; the text field above is what people read. */}
            <input
              ref={picker}
              type="date"
              tabIndex={-1}
              aria-hidden="true"
              className="pointer-events-none absolute bottom-0 right-0 size-0 opacity-0"
              min={min}
              max={max}
              value={value}
              onChange={(e) => onChange(e.target.value)}
            />
          </>
        )}
      </div>
      {error && (
        <p id={errorId} className="mt-1 text-sm text-rose-700">
          {error}
        </p>
      )}
    </>
  )
}
