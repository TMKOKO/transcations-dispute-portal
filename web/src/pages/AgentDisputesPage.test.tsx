import { fireEvent, screen, waitFor, within } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { describe, expect, it, vi } from 'vitest'
import { paths } from '../lib/routes'
import { renderApp } from '../test/render'
import { agentDispute, api, paged, server } from '../test/server'

const openDialog = async (user: ReturnType<typeof renderApp>['user']) => {
  await user.click(await screen.findByRole('button', { name: `Update status of ${agentDispute.disputeNumber}` }))
  return screen.getByRole('dialog', { name: `Update ${agentDispute.disputeNumber}` })
}

describe('AgentDisputesPage', () => {
  it('lists disputes with their status', async () => {
    renderApp(paths.agentDisputes, { role: 'agent' })

    const list = await screen.findByRole('list', { name: 'Disputes' })
    expect(within(list).getByText(agentDispute.disputeNumber)).toBeInTheDocument()
    expect(within(list).getByText('Takealot.com')).toBeInTheDocument()
    expect(within(list).getByText('Submitted')).toBeInTheDocument()
  })

  it('sends the decision and note, then says where it went', async () => {
    let sent: unknown
    server.use(
      http.post(api('/api/agent/disputes/decision'), async ({ request }) => {
        sent = await request.json()
        return HttpResponse.json({ eventId: 'evt-1', eventType: 'UnderReview', topic: 'dispute-under-review' }, { status: 202 })
      }),
    )
    const { user } = renderApp(paths.agentDisputes, { role: 'agent' })

    const dialog = await openDialog(user)
    await user.type(within(dialog).getByLabelText('Note for the customer (optional)'), 'Looking into it.')
    await user.click(within(dialog).getByRole('button', { name: 'Send decision' }))

    const status = await screen.findByRole('status')
    expect(status).toHaveTextContent('Sent UnderReview to dispute-under-review')
    expect(status).toHaveTextContent('Event evt-1')
    expect(sent).toEqual({ disputeId: agentDispute.id, decision: 'UnderReview', note: 'Looking into it.' })
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('warns about a move that will fail, but still sends it', async () => {
    let sent: { decision?: string } = {}
    server.use(
      http.post(api('/api/agent/disputes/decision'), async ({ request }) => {
        sent = (await request.json()) as typeof sent
        return HttpResponse.json({ eventId: 'evt-2', eventType: 'RefundIssued', topic: 'dispute-refund-issued' }, { status: 202 })
      }),
    )
    const { user } = renderApp(paths.agentDisputes, { role: 'agent' })

    const dialog = await openDialog(user)
    expect(within(dialog).queryByRole('alert')).not.toBeInTheDocument()

    await user.selectOptions(within(dialog).getByLabelText('Decision'), 'RefundIssued')
    expect(within(dialog).getByRole('alert')).toHaveTextContent('dispute-decisions-dlq')
    expect(within(dialog).getByRole('alert')).toHaveTextContent('Send Under review first')

    await user.click(within(dialog).getByRole('button', { name: 'Send decision' }))
    expect(await screen.findByRole('status')).toHaveTextContent('dispute-refund-issued')
    expect(sent.decision).toBe('RefundIssued')
  })

  it('warns that a decision on a closed dispute will be skipped', async () => {
    server.use(http.get(api('/api/agent/disputes'), () => HttpResponse.json(paged([{ ...agentDispute, status: 'Resolved' }]))))
    const { user } = renderApp(paths.agentDisputes, { role: 'agent' })

    const dialog = await openDialog(user)
    expect(within(dialog).getByRole('alert')).toHaveTextContent('skip it as stale')
  })

  it('checks the note before calling the API', async () => {
    let calls = 0
    server.use(
      http.post(api('/api/agent/disputes/decision'), () => {
        calls++
        return HttpResponse.json({}, { status: 202 })
      }),
    )
    const { user } = renderApp(paths.agentDisputes, { role: 'agent' })

    const dialog = await openDialog(user)
    const note = within(dialog).getByLabelText('Note for the customer (optional)')
    await user.click(note)
    await user.paste('Bad\u0000note')
    await user.click(within(dialog).getByRole('button', { name: 'Send decision' }))

    expect(await within(dialog).findByText(/special or invisible characters/)).toBeInTheDocument()
    expect(calls).toBe(0)
  })

  it('picks up the status change after a decision without a manual refresh', async () => {
    let status = 'Submitted'
    server.use(
      http.get(api('/api/agent/disputes'), () => HttpResponse.json(paged([{ ...agentDispute, status }]))),
      http.post(api('/api/agent/disputes/decision'), () => {
        // The consumer applies it a moment after the 202.
        setTimeout(() => (status = 'UnderReview'), 50)
        return HttpResponse.json({ eventId: 'evt-1', eventType: 'UnderReview', topic: 'dispute-under-review' }, { status: 202 })
      }),
    )
    const { user } = renderApp(paths.agentDisputes, { role: 'agent' })

    const dialog = await openDialog(user)
    await user.click(within(dialog).getByRole('button', { name: 'Send decision' }))

    const list = await screen.findByRole('list', { name: 'Disputes' })
    await waitFor(() => expect(within(list).getByText('Under review')).toBeInTheDocument(), { timeout: 3000 })
  })
})

describe('role routing', () => {
  it('sends an agent away from customer pages', async () => {
    renderApp(paths.transactions, { role: 'agent' })

    expect(await screen.findByRole('heading', { name: 'All disputes' })).toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'Transactions' })).not.toBeInTheDocument()
  })

  it('sends a customer away from the agent page', async () => {
    renderApp(paths.agentDisputes)

    expect(await screen.findByRole('heading', { name: 'Transactions' })).toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'All disputes' })).not.toBeInTheDocument()
  })

  it('lands an agent on the agent page after signing in', async () => {
    server.use(
      http.post(api('/api/auth/login'), () =>
        HttpResponse.json({ displayName: 'Disputes Agent', role: 'agent', idleTimeoutSeconds: 300 }),
      ),
    )
    const { user } = renderApp('/login', { signedIn: false })

    await user.type(screen.getByLabelText('Email address'), 'agent@example.com')
    await user.type(screen.getByLabelText('Password'), 'Demo@12345!')
    await user.click(screen.getByRole('button', { name: 'Sign in' }))

    expect(await screen.findByRole('heading', { name: 'All disputes' })).toBeInTheDocument()
  })

  it('keeps an agent signed in through the agent endpoint, since /api/me is for customers', async () => {
    let calls = 0
    server.use(
      http.get(api('/api/agent/session'), () => {
        calls++
        return new HttpResponse(null, { status: 204 })
      }),
    )
    vi.useFakeTimers({ shouldAdvanceTime: true })
    try {
      renderApp(paths.agentDisputes, { role: 'agent' })
      await screen.findByRole('list', { name: 'Disputes' })
      await vi.advanceTimersByTimeAsync(4 * 60_000 + 5_000)

      fireEvent.click(await screen.findByRole('button', { name: 'Stay signed in' }))

      await waitFor(() => expect(calls).toBe(1))
    } finally {
      vi.useRealTimers()
    }
  })

  it('treats a session stored before roles existed as a customer', async () => {
    sessionStorage.setItem(
      'dispute-portal.session',
      JSON.stringify({ displayName: 'Thandi', idleTimeoutSeconds: 300, idleExpiresAt: Date.now() + 300_000 }),
    )
    renderApp(paths.transactions, { signedIn: false })

    expect(await screen.findByRole('heading', { name: 'Transactions' })).toBeInTheDocument()
  })
})
