import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { configureAuth, request } from '../api/client'
import type { LoginResponse } from '../api/types'

// sessionStorage, not localStorage: the session ends with the tab, and the token is short-lived
// (30 min). The trade-off against an httpOnly cookie is recorded in docs/adr/0003.
const STORAGE_KEY = 'dispute-portal.session'

interface Session {
  token: string
  expiresAt: string
  displayName: string
}

interface AuthState {
  session: Session | null
  isAuthenticated: boolean
  /** Set when the session ended on its own (expiry, 401) so the login page can explain why. */
  signedOutReason: string | null
  login: (email: string, password: string) => Promise<void>
  logout: (reason?: string) => void
}

const AuthContext = createContext<AuthState | null>(null)

function readStoredSession(): Session | null {
  try {
    const raw = sessionStorage.getItem(STORAGE_KEY)
    if (!raw) return null
    const session = JSON.parse(raw) as Session
    return new Date(session.expiresAt).getTime() > Date.now() ? session : null
  } catch {
    return null
  }
}

function storeSession(session: Session | null) {
  try {
    if (session) sessionStorage.setItem(STORAGE_KEY, JSON.stringify(session))
    else sessionStorage.removeItem(STORAGE_KEY)
  } catch {
    // Storage blocked (private mode, policy): the session still works for this page load.
  }
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const queryClient = useQueryClient()
  const [session, setSession] = useState<Session | null>(readStoredSession)
  const [signedOutReason, setSignedOutReason] = useState<string | null>(null)

  const logout = useCallback(
    (reason?: string) => {
      storeSession(null)
      setSession(null)
      setSignedOutReason(reason ?? null)
      // Never let one customer's cached transactions survive into the next sign-in.
      queryClient.clear()
    },
    [queryClient],
  )

  // Configured during render, not in an effect: child queries fire in their own effects, which
  // run before this component's, and must already see the token.
  configureAuth(
    () => session?.token ?? null,
    () => logout('Your session has ended. Please sign in again.'),
  )

  // Sign out at expiry rather than waiting for the next request to fail.
  useEffect(() => {
    if (!session) return
    const ms = new Date(session.expiresAt).getTime() - Date.now()
    const timer = window.setTimeout(() => logout('Your session expired. Please sign in again.'), Math.max(ms, 0))
    return () => window.clearTimeout(timer)
  }, [session, logout])

  const login = useCallback(async (email: string, password: string) => {
    const { data } = await request<LoginResponse>('/api/auth/login', {
      method: 'POST',
      body: { email, password },
      skipAuthRedirect: true,
    })
    const next = { token: data.token, expiresAt: data.expiresAt, displayName: data.displayName }
    storeSession(next)
    setSignedOutReason(null)
    setSession(next)
  }, [])

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
