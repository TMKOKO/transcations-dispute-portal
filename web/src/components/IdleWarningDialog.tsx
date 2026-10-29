import { useEffect, useState } from 'react'
import { ApiError } from '../api/client'
import { useAuth } from '../auth/AuthContext'
import { ConfirmDialog } from './ConfirmDialog'

/**
 * "Still there?" a minute before the idle sign-out. The server only sees requests, not typing, so
 * a customer writing a long dispute description would otherwise lose it without warning.
 * Being a modal, it keeps the page (and any half-filled form) underneath untouched.
 */
export function IdleWarningDialog() {
  const { idleWarningUntil, stayActive, logout } = useAuth()
  const [now, setNow] = useState(() => Date.now())
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (idleWarningUntil === null) return
    setNow(Date.now())
    setError(null)
    const tick = window.setInterval(() => setNow(Date.now()), 1000)
    return () => window.clearInterval(tick)
  }, [idleWarningUntil])

  const seconds = idleWarningUntil === null ? 0 : Math.max(0, Math.ceil((idleWarningUntil - now) / 1000))

  async function stay() {
    setBusy(true)
    setError(null)
    try {
      await stayActive()
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Something went wrong. Please try again.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <ConfirmDialog
      open={idleWarningUntil !== null}
      title="Are you still there?"
      confirmLabel="Stay signed in"
      cancelLabel="Sign out"
      busy={busy}
      onConfirm={() => void stay()}
      onCancel={() => logout()}
      // Pressing a key at all means someone is there.
      onEscape={() => void stay()}
    >
      <p>
        For your security, you'll be signed out in{' '}
        <strong className="tabular-nums">
          {seconds} second{seconds === 1 ? '' : 's'}
        </strong>{' '}
        because there has been no activity.
      </p>
      {error && (
        <p role="alert" className="mt-3 rounded-md bg-rose-50 px-3 py-2 text-rose-800">
          {error}
        </p>
      )}
    </ConfirmDialog>
  )
}
