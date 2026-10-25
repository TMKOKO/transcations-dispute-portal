import { useState, type FormEvent } from 'react'
import { Navigate, useLocation, useNavigate } from 'react-router'
import { ApiError } from '../api/client'
import { useAuth } from '../auth/AuthContext'
import { useDocumentTitle } from '../lib/useDocumentTitle'

// Shown on local/demo builds only (see web/README.md). Never set in a real deployment.
const showDemoHint = import.meta.env.DEV || import.meta.env.VITE_DEMO_HINT === 'true'

export function LoginPage() {
  useDocumentTitle('Sign in')
  const { login, isAuthenticated, signedOutReason } = useAuth()
  const navigate = useNavigate()
  const location = useLocation()
  const { from = '/transactions', fromState } = (location.state as { from?: string; fromState?: unknown } | null) ?? {}

  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  if (isAuthenticated) return <Navigate to={from} replace state={fromState} />

  async function onSubmit(e: FormEvent) {
    e.preventDefault()
    setError(null)
    if (!email.trim() || !password) {
      setError('Enter your email address and password.')
      return
    }
    setBusy(true)
    try {
      await login(email.trim(), password)
      navigate(from, { replace: true, state: fromState })
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Something went wrong. Please try again.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="flex min-h-dvh items-center justify-center bg-gradient-to-br from-brand-900 to-brand-700 px-4 py-12">
      <main className="w-full max-w-sm">
        <div className="mb-6 flex items-center justify-center gap-2 text-white">
          <img src="/favicon.svg" alt="" className="size-9" />
          <span className="text-xl font-semibold tracking-tight">Dispute Portal</span>
        </div>
        <div className="rounded-2xl bg-white p-6 shadow-xl sm:p-8">
          <h1 className="text-lg font-semibold text-slate-900">Sign in</h1>
          <p className="mt-1 text-sm text-slate-600">View your transactions and raise or track a dispute.</p>

          {signedOutReason && !error && (
            <p role="status" className="mt-4 rounded-md bg-amber-50 px-3 py-2 text-sm text-amber-800">
              {signedOutReason}
            </p>
          )}
          {error && (
            <p role="alert" className="mt-4 rounded-md bg-rose-50 px-3 py-2 text-sm text-rose-800">
              {error}
            </p>
          )}

          <form onSubmit={onSubmit} noValidate className="mt-5 space-y-4">
            <div>
              <label htmlFor="email" className="label">
                Email address
              </label>
              <input
                id="email"
                type="email"
                autoComplete="username"
                className="input"
                value={email}
                onChange={(e) => setEmail(e.target.value)}
                required
              />
            </div>
            <div>
              <label htmlFor="password" className="label">
                Password
              </label>
              <input
                id="password"
                type="password"
                autoComplete="current-password"
                className="input"
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                required
              />
            </div>
            <button type="submit" className="btn-primary w-full" disabled={busy}>
              {busy ? 'Signing in…' : 'Sign in'}
            </button>
          </form>

          {showDemoHint && (
            <div className="mt-6 rounded-md border border-dashed border-slate-300 p-3 text-xs text-slate-600">
              <p className="font-medium text-slate-700">Demo accounts</p>
              <p className="mt-1">
                thandi@example.com, sipho@example.com or lerato@example.com — password <code>Demo@12345!</code>
              </p>
            </div>
          )}
        </div>
      </main>
    </div>
  )
}
