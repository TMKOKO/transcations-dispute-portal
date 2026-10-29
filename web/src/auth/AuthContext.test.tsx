import { act, fireEvent, screen, waitFor, within } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { describe, expect, it, vi } from 'vitest'
import { renderApp } from '../test/render'
import { api, eligibleTx, paged, server } from '../test/server'

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

  it('signs out the moment a background tab comes back after the deadline, even if its timer is late', async () => {
    // Fake timers that are never advanced stand in for a throttled background timer.
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'Date'], shouldAdvanceTime: true })
    try {
      renderApp('/transactions')
      await screen.findByRole('heading', { name: 'Transactions' })

      vi.setSystemTime(Date.now() + 10 * 60_000)
      act(() => {
        document.dispatchEvent(new Event('visibilitychange'))
      })

      expect(await screen.findByRole('heading', { name: 'Sign in' })).toBeInTheDocument()
      expect(screen.getByRole('status')).toHaveTextContent('signed out after 5 minutes without activity')
    } finally {
      vi.useRealTimers()
    }
  })

  it('does not call the API by itself when the customer returns to the tab', async () => {
    let calls = 0
    server.use(
      http.get(api('/api/transactions'), () => {
        calls++
        return HttpResponse.json(paged([eligibleTx]))
      }),
    )
    vi.useFakeTimers({ toFake: ['Date'], shouldAdvanceTime: true })
    try {
      renderApp('/transactions')
      await screen.findByRole('heading', { name: 'Transactions' })
      expect(calls).toBe(1)

      // Data is stale (older than 30 s) but the session is not: a refetch here would be the
      // portal, not the customer, keeping the session alive.
      vi.setSystemTime(Date.now() + 2 * 60_000)
      act(() => {
        document.dispatchEvent(new Event('visibilitychange'))
        window.dispatchEvent(new Event('visibilitychange'))
        window.dispatchEvent(new Event('focus'))
        window.dispatchEvent(new Event('online'))
      })
      await new Promise((resolve) => setTimeout(resolve, 50))

      expect(calls).toBe(1)
      expect(screen.getByRole('heading', { name: 'Transactions' })).toBeInTheDocument()
    } finally {
      vi.useRealTimers()
    }
  })

  describe('"still there?" warning', () => {
    const warning = () => screen.queryByRole('dialog', { name: 'Are you still there?' })
    const openWarning = () => screen.findByRole('dialog', { name: 'Are you still there?' })

    it('appears a minute before the idle sign-out with a countdown', async () => {
      vi.useFakeTimers({ shouldAdvanceTime: true })
      try {
        renderApp('/transactions')
        await screen.findByRole('heading', { name: 'Transactions' })

        await vi.advanceTimersByTimeAsync(3 * 60_000 + 50_000)
        expect(warning()).toBeNull()

        const deadline = storedDeadline()
        await vi.advanceTimersByTimeAsync(15_000)
        const dialog = await openWarning()
        expect(dialog).toHaveTextContent(/signed out in 5\d seconds/)

        await vi.advanceTimersByTimeAsync(20_000)
        await waitFor(() => expect(dialog).toHaveTextContent(/signed out in 3\d seconds/))
        // Showing the warning is not activity: it must not have called the API and moved the deadline.
        expect(storedDeadline()).toBe(deadline)
      } finally {
        vi.useRealTimers()
      }
    })

    it('"Stay signed in" calls the API, which restarts the idle timer', async () => {
      let pinged = 0
      server.use(
        http.get(api('/api/me'), () => {
          pinged++
          return HttpResponse.json({ id: '1', firstName: 'Thandi', lastName: 'M', email: 'x@example.com' })
        }),
      )
      vi.useFakeTimers({ shouldAdvanceTime: true })
      try {
        renderApp('/transactions')
        await screen.findByRole('heading', { name: 'Transactions' })
        await vi.advanceTimersByTimeAsync(4 * 60_000 + 5_000)
        const before = storedDeadline()!

        fireEvent.click(within(await openWarning()).getByRole('button', { name: 'Stay signed in' }))

        await waitFor(() => expect(warning()).toBeNull())
        expect(pinged).toBe(1)
        expect(storedDeadline()).toBeGreaterThan(before)

        // Well past the old deadline: still signed in.
        await vi.advanceTimersByTimeAsync(2 * 60_000)
        expect(screen.getByRole('heading', { name: 'Transactions' })).toBeInTheDocument()
      } finally {
        vi.useRealTimers()
      }
    })

    it('Escape also keeps the customer signed in', async () => {
      server.use(http.get(api('/api/me'), () => HttpResponse.json({ id: '1' })))
      vi.useFakeTimers({ shouldAdvanceTime: true })
      try {
        renderApp('/transactions')
        await screen.findByRole('heading', { name: 'Transactions' })
        await vi.advanceTimersByTimeAsync(4 * 60_000 + 5_000)

        fireEvent(await openWarning(), new Event('cancel', { cancelable: true }))

        await waitFor(() => expect(warning()).toBeNull())
        expect(screen.getByRole('heading', { name: 'Transactions' })).toBeInTheDocument()
      } finally {
        vi.useRealTimers()
      }
    })

    it('"Sign out" signs out straight away', async () => {
      vi.useFakeTimers({ shouldAdvanceTime: true })
      try {
        renderApp('/transactions')
        await screen.findByRole('heading', { name: 'Transactions' })
        await vi.advanceTimersByTimeAsync(4 * 60_000 + 5_000)

        fireEvent.click(within(await openWarning()).getByRole('button', { name: 'Sign out' }))

        expect(await screen.findByRole('heading', { name: 'Sign in' })).toBeInTheDocument()
      } finally {
        vi.useRealTimers()
      }
    })

    it('shows the error and stays open if the API cannot be reached', async () => {
      server.use(
        http.get(api('/api/me'), () =>
          HttpResponse.json(
            { status: 503, detail: "We can't reach the sign-in service right now.", code: 'session_store_unavailable' },
            { status: 503 },
          ),
        ),
      )
      vi.useFakeTimers({ shouldAdvanceTime: true })
      try {
        renderApp('/transactions')
        await screen.findByRole('heading', { name: 'Transactions' })
        await vi.advanceTimersByTimeAsync(4 * 60_000 + 5_000)

        fireEvent.click(within(await openWarning()).getByRole('button', { name: 'Stay signed in' }))

        expect(await screen.findByRole('alert')).toHaveTextContent("We can't reach the sign-in service")
        expect(warning()).toBeInTheDocument()
      } finally {
        vi.useRealTimers()
      }
    })
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
