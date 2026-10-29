import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { configureAuth, request } from '../api/client'
import type { LoginResponse } from '../api/types'

// The session itself is an httpOnly cookie the API sets; JavaScript never sees it. This is only
// what the portal needs to render after a reload: the name, and when the idle timer runs out.
// sessionStorage, not localStorage, so it ends with the tab.
const STORAGE_KEY = 'dispute-portal.session'

interface Session {
  displayName: string
  idleTimeoutSeconds: number
}

interface StoredSession extends Session {
  /** Epoch ms. Pushed back after every API response, like the server's own idle timer. */
  idleExpiresAt: number
}

interface AuthState {
  session: Session | null
  isAuthenticated: boolean
  /** Set when the session ended on its own (idle timeout, 401) so the login page can explain why. */
  signedOutReason: string | null
  login: (email: string, password: string) => Promise<void>
  logout: (reason?: string) => void
}

const AuthContext = createContext<AuthState | null>(null)

function readStoredSession(): StoredSession | null {
  try {
    const raw = sessionStorage.getItem(STORAGE_KEY)
    if (!raw) return null
    const stored = JSON.parse(raw) as StoredSession
    return stored.idleExpiresAt > Date.now() ? stored : null
  } catch {
    return null
  }
}

function storeSession(session: StoredSession | null) {
  try {
    if (session) sessionStorage.setItem(STORAGE_KEY, JSON.stringify(session))
    else sessionStorage.removeItem(STORAGE_KEY)
  } catch {
    // Storage blocked (private mode, policy): the session still works for this page load.
  }
}

function idleMessage(session: Session) {
  const minutes = Math.round(session.idleTimeoutSeconds / 60)
  return `You were signed out after ${minutes} minute${minutes === 1 ? '' : 's'} without activity.`
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const queryClient = useQueryClient()
  const [session, setSession] = useState<Session | null>(() => {
    const stored = readStoredSession()
    return stored && { displayName: stored.displayName, idleTimeoutSeconds: stored.idleTimeoutSeconds }
  })
  const [signedOutReason, setSignedOutReason] = useState<string | null>(null)
  const idleTimer = useRef<number | undefined>(undefined)

  const logout = useCallback(
    (reason?: string) => {
      window.clearTimeout(idleTimer.current)
      // Best effort: revokes the session in Redis and clears the cookie. If the API is unreachable
      // the session still ends on its own at the idle timeout.
      request('/api/auth/logout', { method: 'POST', skipAuthRedirect: true }).catch(() => {})
      storeSession(null)
      setSession(null)
      setSignedOutReason(reason ?? null)
      // Never let one customer's cached transactions survive into the next sign-in.
      queryClient.clear()
    },
    [queryClient],
  )

  // Mirrors the server's idle timer, so the portal signs out when the session ends rather than
  // waiting for the next request to fail. The server stays the authority: a 401 also signs out.
  const scheduleIdleLogout = useCallback(
    (current: Session, idleExpiresAt: number) => {
      storeSession({ ...current, idleExpiresAt })
      window.clearTimeout(idleTimer.current)
      idleTimer.current = window.setTimeout(() => logout(idleMessage(current)), Math.max(idleExpiresAt - Date.now(), 0))
    },
    [logout],
  )

  // Configured during render, not in an effect: child queries fire in their own effects, which
  // run before this component's, and must already see the handlers.
  configureAuth(
    () => logout('Your session has ended. Please sign in again.'),
    () => {
      if (session) scheduleIdleLogout(session, Date.now() + session.idleTimeoutSeconds * 1000)
    },
  )

  // A session restored after a reload keeps the deadline it already had.
  useEffect(() => {
    const stored = readStoredSession()
    if (stored) scheduleIdleLogout(stored, stored.idleExpiresAt)
    return () => window.clearTimeout(idleTimer.current)
  }, [scheduleIdleLogout])

  const login = useCallback(
    async (email: string, password: string) => {
      const { data } = await request<LoginResponse>('/api/auth/login', {
        method: 'POST',
        body: { email, password },
        skipAuthRedirect: true,
      })
      const next = { displayName: data.displayName, idleTimeoutSeconds: data.idleTimeoutSeconds }
      scheduleIdleLogout(next, Date.now() + next.idleTimeoutSeconds * 1000)
      setSignedOutReason(null)
      setSession(next)
    },
    [scheduleIdleLogout],
  )

  const value = useMemo<AuthState>(
    () => ({ session, isAuthenticated: session !== null, signedOutReason, login, logout }),
    [session, signedOutReason, login, logout],
  )

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}

// eslint-disable-next-line react-refresh/only-export-components
export function useAuth(): AuthState {
  const ctx = useContext(AuthContext)
  if (!ctx) throw new Error('useAuth must be used inside <AuthProvider>')
  return ctx
}
