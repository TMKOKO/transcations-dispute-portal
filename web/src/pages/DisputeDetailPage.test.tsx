import { screen, within } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'
import { paths } from '../lib/routes'
import { renderApp } from '../test/render'
import { api, disputeDetail, server } from '../test/server'

describe('DisputeDetailPage', () => {
  it('shows status, details and the timeline in order', async () => {
    renderApp({ pathname: paths.disputeDetail, state: { disputeId: 'd-1' } })

    const heading = await screen.findByRole('heading', { name: 'DSP-2026-001000' })
    expect(within(heading.parentElement!).getByText('Under review')).toBeInTheDocument()
    expect(screen.getByText(disputeDetail.description)).toBeInTheDocument()

    const items = within(screen.getByRole('list', { name: 'Dispute timeline' })).getAllByRole('listitem')
    expect(items).toHaveLength(2)
    expect(items[0]).toHaveTextContent('Dispute raised')
    expect(items[0]).toHaveTextContent('You')
    expect(items[1]).toHaveTextContent('Under review')
    expect(items[1]).toHaveTextContent('Disputes team')
    expect(items[1]).toHaveTextContent('assigned to a disputes specialist')
  })

  it('withdraws after confirmation, sending the optional note', async () => {
    let sent: unknown
    server.use(
      http.post(api('/api/disputes/withdraw'), async ({ request }) => {
        sent = await request.json()
        return HttpResponse.json({
          ...disputeDetail,
          status: 'Withdrawn',
          canWithdraw: false,
          timeline: [
            ...disputeDetail.timeline,
            { fromStatus: 'UnderReview', toStatus: 'Withdrawn', note: 'Refunded directly.', changedBy: 'customer', changedAt: '2026-10-04T08:00:00Z' },
          ],
        })
      }),
    )
    const { user } = renderApp({ pathname: paths.disputeDetail, state: { disputeId: 'd-1' } })

    await user.click(await screen.findByRole('button', { name: 'Withdraw dispute' }))
    const dialog = screen.getByRole('dialog', { name: 'Withdraw this dispute?' })
    await user.type(within(dialog).getByLabelText('Reason (optional)'), 'Refunded directly.')
    await user.click(within(dialog).getByRole('button', { name: 'Withdraw dispute' }))

    expect(await screen.findByText('Your dispute has been withdrawn.')).toBeInTheDocument()
    expect(sent).toEqual({ disputeId: 'd-1', note: 'Refunded directly.' })
    expect(screen.getByText('You withdrew this dispute.')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Withdraw dispute' })).not.toBeInTheDocument()
  })

  it('hides Withdraw for closed disputes', async () => {
    server.use(http.post(api('/api/disputes/lookup'), () => HttpResponse.json({ ...disputeDetail, status: 'Resolved', canWithdraw: false })))
    renderApp({ pathname: paths.disputeDetail, state: { disputeId: 'd-1' } })

    expect(await screen.findByText('This dispute was resolved in your favour.')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Withdraw dispute' })).not.toBeInTheDocument()
  })

  it('shows not found for an unknown dispute', async () => {
    server.use(http.post(api('/api/disputes/lookup'), () => HttpResponse.json({ status: 404, detail: 'Dispute not found.' }, { status: 404 })))
    renderApp({ pathname: paths.disputeDetail, state: { disputeId: 'missing' } })

    expect(await screen.findByRole('alert')).toHaveTextContent('We could not find that dispute.')
  })

  it('sends a page opened without a dispute (bookmark, new tab) back to the list', async () => {
    renderApp(paths.disputeDetail)
    expect(await screen.findByRole('heading', { name: 'My disputes' })).toBeInTheDocument()
  })

  it('no longer has a page at an address with the id in it', async () => {
    renderApp('/disputes/d-1')
    expect(await screen.findByText("We couldn't find that page")).toBeInTheDocument()
  })

  it('keeps the dispute across a sign-in detour', async () => {
    server.use(
      http.post(api('/api/auth/login'), () =>
        HttpResponse.json({ displayName: 'Thandi', role: 'customer', idleTimeoutSeconds: 300 }),
      ),
    )
    const { user } = renderApp({ pathname: paths.disputeDetail, state: { disputeId: 'd-1' } }, { signedIn: false })

    await user.type(await screen.findByLabelText('Email address'), 'thandi@example.com')
    await user.type(screen.getByLabelText('Password'), 'Demo@12345!')
    await user.click(screen.getByRole('button', { name: 'Sign in' }))

    expect(await screen.findByRole('heading', { name: 'DSP-2026-001000' })).toBeInTheDocument()
  })
})
