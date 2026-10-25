import { screen, waitFor, within } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'
import { renderApp } from '../test/render'
import { api, eligibleTx, paged, server } from '../test/server'

describe('TransactionsPage', () => {
  it('redirects to sign-in when there is no session', async () => {
    renderApp('/transactions', { signedIn: false })
    expect(await screen.findByRole('heading', { name: 'Sign in' })).toBeInTheDocument()
  })

  it('offers Dispute only on eligible rows and explains the rest', async () => {
    renderApp('/transactions')

    const list = await screen.findByRole('list', { name: 'Transactions' })
    const rows = within(list).getAllByRole('listitem')
    expect(rows).toHaveLength(3)

    const [eligible, open, old] = rows as [HTMLElement, HTMLElement, HTMLElement]
    expect(within(eligible).getByRole('link', { name: /dispute computicket/i })).toHaveAttribute(
      'href',
      '/transactions/tx-eligible/dispute',
    )
    expect(within(eligible).getByText(/R\s?850[.,]00/)).toBeInTheDocument()

    expect(within(open).queryByRole('link', { name: /^dispute/i })).not.toBeInTheDocument()
    expect(within(open).getByRole('link', { name: /view dsp-2026-001000/i })).toHaveAttribute('href', '/disputes/d-1')
    expect(within(open).getByText('Under review')).toBeInTheDocument()

    expect(within(old).getByText('Transactions can only be disputed within 120 days.')).toBeInTheDocument()
  })

  it('sends the search term to the API after typing stops', async () => {
    const seen: string[] = []
    server.use(
      http.get(api('/api/transactions'), ({ request }) => {
        seen.push(new URL(request.url).searchParams.get('search') ?? '')
        return HttpResponse.json(paged([eligibleTx]))
      }),
    )
    const { user } = renderApp('/transactions')
    await screen.findByRole('list', { name: 'Transactions' })

    await user.type(screen.getByLabelText('Search'), 'compu')

    await waitFor(() => expect(seen).toContain('compu'))
    // Debounced: not one request per keystroke.
    expect(seen.filter((s) => s.startsWith('c')).length).toBeLessThanOrEqual(2)
  })

  it('shows an empty state for filters with no results', async () => {
    server.use(http.get(api('/api/transactions'), () => HttpResponse.json(paged([]))))
    renderApp('/transactions?search=nothing')

    expect(await screen.findByText('No transactions match your filters')).toBeInTheDocument()
  })

  it('shows the server error with a retry', async () => {
    server.use(
      http.get(api('/api/transactions'), () => HttpResponse.json({ status: 500, title: 'Server error' }, { status: 500 })),
    )
    renderApp('/transactions')

    expect(await screen.findByRole('alert')).toHaveTextContent('Server error')
    expect(screen.getByRole('button', { name: 'Try again' })).toBeInTheDocument()
  })
})
