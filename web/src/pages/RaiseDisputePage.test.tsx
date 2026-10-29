import { screen } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'
import type { RaiseDisputeBody } from '../api/types'
import { paths } from '../lib/routes'
import { renderApp } from '../test/render'
import { api, disputeDetail, server } from '../test/server'

const validDescription = 'I was charged twice for the same concert ticket.'

/** The transaction id rides in router state, as the Dispute links on the transactions page send it. */
const raiseDisputeRoute = (transactionId: string) => ({ pathname: paths.raiseDispute, state: { transactionId } })

async function fillAndContinue(user: ReturnType<typeof renderApp>['user'], amount?: string) {
  await user.click(await screen.findByRole('radio', { name: /charged more than once/i }))
  await user.type(screen.getByLabelText('Describe what happened'), validDescription)
  if (amount !== undefined) {
    const input = screen.getByLabelText("Amount you're disputing")
    await user.clear(input)
    await user.type(input, amount)
  }
  await user.click(screen.getByRole('button', { name: 'Continue' }))
}

describe('RaiseDisputePage', () => {
  it('validates on Continue and points at each problem', async () => {
    const { user } = renderApp(raiseDisputeRoute('tx-eligible'))
    await screen.findByRole('heading', { name: 'Raise a dispute' })

    const amount = screen.getByLabelText("Amount you're disputing")
    expect(amount).toHaveValue('850.00') // defaults to the full amount
    await user.clear(amount)
    await user.type(amount, '900')
    await user.type(screen.getByLabelText('Describe what happened'), 'too short')
    await user.click(screen.getByRole('button', { name: 'Continue' }))

    expect(screen.getByText('Choose the reason that best describes the problem.')).toBeInTheDocument()
    expect(screen.getByText(/at least 20 characters/)).toBeInTheDocument()
    expect(screen.getByText('You cannot dispute more than the transaction amount.')).toBeInTheDocument()
    expect(screen.getByLabelText('Describe what happened')).toHaveAttribute('aria-invalid', 'true')
  })

  it('flags invisible characters pasted into the description', async () => {
    const { user } = renderApp(raiseDisputeRoute('tx-eligible'))
    await user.click(await screen.findByRole('radio', { name: /charged more than once/i }))
    const description = screen.getByLabelText('Describe what happened')
    await user.click(description)
    // U+202E flips the text that follows, so what an agent reads is not what was typed.
    await user.paste(`I was charged twice ‮for the same concert ticket.`)
    await user.click(screen.getByRole('button', { name: 'Continue' }))

    expect(description).toHaveAttribute('aria-invalid', 'true')
    expect(screen.getByText(/special or invisible characters/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Continue' })).toBeInTheDocument()
  })

  it('reviews then submits with an idempotency key, and shows the reference', async () => {
    let body: RaiseDisputeBody | undefined
    let key: string | null = null
    server.use(
      http.post(api('/api/disputes'), async ({ request }) => {
        body = (await request.json()) as RaiseDisputeBody
        key = request.headers.get('Idempotency-Key')
        return HttpResponse.json({ ...disputeDetail, id: 'd-new', disputeNumber: 'DSP-2026-001042', status: 'Submitted' }, { status: 201 })
      }),
    )
    const { user } = renderApp(raiseDisputeRoute('tx-eligible'))

    await fillAndContinue(user, '425.5')

    expect(await screen.findByText('Step 2 of 2 · Check and submit')).toBeInTheDocument()
    expect(screen.getByText(/R\s?425[.,]50/)).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Submit dispute' }))

    expect(await screen.findByRole('heading', { name: 'Dispute submitted' })).toBeInTheDocument()
    expect(screen.getByText('DSP-2026-001042')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Track this dispute' })).toHaveAttribute('href', '/disputes/detail')
    expect(body).toEqual({
      transactionId: 'tx-eligible',
      reason: 'DuplicateCharge',
      description: validDescription,
      disputedAmount: 425.5,
    })
    expect(key).toMatch(/^web-[0-9a-f-]{36}$/)
  })

  it('reuses the same idempotency key when the customer retries after a failure', async () => {
    const keys: (string | null)[] = []
    server.use(
      http.post(api('/api/disputes'), ({ request }) => {
        keys.push(request.headers.get('Idempotency-Key'))
        return keys.length === 1
          ? HttpResponse.error()
          : HttpResponse.json({ ...disputeDetail, status: 'Submitted' }, { status: 201 })
      }),
    )
    const { user } = renderApp(raiseDisputeRoute('tx-eligible'))
    await fillAndContinue(user)

    await user.click(await screen.findByRole('button', { name: 'Submit dispute' }))
    expect(await screen.findByRole('alert')).toHaveTextContent(/could not reach the server/i)
    await user.click(screen.getByRole('button', { name: 'Try again' }))

    expect(await screen.findByRole('heading', { name: 'Dispute submitted' })).toBeInTheDocument()
    expect(keys).toHaveLength(2)
    expect(keys[0]).toBe(keys[1])
  })

  it("surfaces the API's reason when it refuses the dispute", async () => {
    server.use(
      http.post(api('/api/disputes'), () =>
        HttpResponse.json(
          { status: 409, title: 'Conflict', detail: 'This transaction already has an open dispute.', code: 'dispute_already_open' },
          { status: 409 },
        ),
      ),
    )
    const { user } = renderApp(raiseDisputeRoute('tx-eligible'))
    await fillAndContinue(user)
    await user.click(await screen.findByRole('button', { name: 'Submit dispute' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('This transaction already has an open dispute.')
    expect(screen.getByRole('link', { name: 'Go to my disputes' })).toBeInTheDocument()
  })

  it('explains why an ineligible transaction cannot be disputed', async () => {
    renderApp(raiseDisputeRoute('tx-old'))

    expect(await screen.findByRole('heading', { name: "This transaction can't be disputed" })).toBeInTheDocument()
    expect(screen.getByText('Transactions can only be disputed within 120 days.')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Continue' })).not.toBeInTheDocument()
  })

  it('handles an unknown transaction', async () => {
    renderApp(raiseDisputeRoute('nope'))
    expect(await screen.findByRole('alert')).toHaveTextContent('We could not find that transaction.')
  })

  it('sends a page opened without a transaction (bookmark, new tab) back to the list', async () => {
    renderApp(paths.raiseDispute)
    expect(await screen.findByRole('heading', { name: 'Transactions' })).toBeInTheDocument()
  })
})
