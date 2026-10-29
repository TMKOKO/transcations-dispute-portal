import { screen, waitFor } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { describe, expect, it, vi } from 'vitest'
import { renderApp } from '../test/render'
import { api, server } from '../test/server'

const STORAGE_KEY = 'dispute-portal.session'

function storeSession(idleExpiresAt: number) {
  sessionStorage.setItem(STORAGE_KEY, JSON.stringify({ displayName: 'Thandi', idleTimeoutSeconds: 300, idleExpiresAt }))
}

function storedDeadline(): number | null {
  const raw = sessionStorage.getItem(STORAGE_KEY)
  return raw ? (JSON.parse(raw) as { idleExpiresAt: number }).idleExpiresAt : null
}

describe('AuthProvider', () => {
  it('keeps no token anywhere the page can read', async () => {
    renderApp('/transactions')
    await screen.findByRole('heading', { name: 'Transactions' })

    expect(sessionStorage.getItem(STORAGE_KEY)).not.toMatch(/token/i)
  })

  it('signs out after five minutes without an API call, and tells the customer why', async () => {
    // shouldAdvanceTime keeps msw and React running; the jump below is the idle period.
    vi.useFakeTimers({ shouldAdvanceTime: true })
    try {
      renderApp('/transactions')
      await screen.findByRole('heading', { name: 'Transactions' })

      await vi.advanceTimersByTimeAsync(4 * 60_000)
      expect(screen.getByRole('heading', { name: 'Transactions' })).toBeInTheDocument()

      await vi.advanceTimersByTimeAsync(60_000 + 1_000)
      expect(await screen.findByRole('heading', { name: 'Sign in' })).toBeInTheDocument()
      expect(screen.getByRole('status')).toHaveTextContent('signed out after 5 minutes without activity')
      expect(sessionStorage.getItem(STORAGE_KEY)).toBeNull()
    } finally {
      vi.useRealTimers()
    }
  })

  it('pushes the idle deadline back on every API response', async () => {
    const soon = Date.now() + 60_000
    storeSession(soon)
    renderApp('/transactions', { signedIn: false })
    await screen.findByRole('heading', { name: 'Transactions' })

    await waitFor(() => expect(storedDeadline()).toBeGreaterThan(soon))
  })

  it('does not restore a session whose idle deadline has passed', async () => {
    storeSession(Date.now() - 1)
    renderApp('/transactions', { signedIn: false })

    expect(await screen.findByRole('heading', { name: 'Sign in' })).toBeInTheDocument()
  })

  it('revokes the session on the server when the customer signs out', async () => {
    let revoked = false
    server.use(
      http.post(api('/api/auth/logout'), () => {
        revoked = true
        return new HttpResponse(null, { status: 204 })
      }),
    )
    const { user } = renderApp('/transactions')
    await screen.findByRole('heading', { name: 'Transactions' })

    await user.click(screen.getByRole('button', { name: 'Sign out' }))

    expect(await screen.findByRole('heading', { name: 'Sign in' })).toBeInTheDocument()
    await waitFor(() => expect(revoked).toBe(true))
  })
})
