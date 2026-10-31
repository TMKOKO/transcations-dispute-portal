import { fireEvent, screen, waitFor, within } from '@testing-library/react'
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
      '/transactions/dispute',
    )
    expect(within(eligible).getByText(/R\s?850[.,]00/)).toBeInTheDocument()

    expect(within(open).queryByRole('link', { name: /^dispute/i })).not.toBeInTheDocument()
    expect(within(open).getByRole('link', { name: /view dsp-2026-001000/i })).toHaveAttribute('href', '/disputes/detail')
    expect(within(open).getByText('Under review')).toBeInTheDocument()

    expect(within(old).getByText('Transactions can only be disputed within 120 days.')).toBeInTheDocument()
  })

  it('opens the dispute form with the transaction id in router state, not the address', async () => {
    let lookedUp: unknown
    server.use(
      http.post(api('/api/transactions/lookup'), async ({ request }) => {
        lookedUp = await request.json()
        return HttpResponse.json(eligibleTx)
      }),
    )
    const { user } = renderApp('/transactions')

    const link = await screen.findByRole('link', { name: /dispute computicket/i })
    expect(link.getAttribute('href')).not.toContain(eligibleTx.id)
    await user.click(link)

    await waitFor(() => expect(lookedUp).toEqual({ transactionId: eligibleTx.id }))
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

  describe('date filters', () => {
    function recordFilters() {
      const seen: { from: string | null; to: string | null }[] = []
      server.use(
        http.get(api('/api/transactions'), ({ request }) => {
          const params = new URL(request.url).searchParams
          seen.push({ from: params.get('from'), to: params.get('to') })
          return HttpResponse.json(paged([eligibleTx]))
        }),
      )
      return seen
    }

    it('reads day/month/year whatever the browser language', async () => {
      renderApp('/transactions')
      await screen.findByRole('list', { name: 'Transactions' })

      expect(screen.getByLabelText('From')).toHaveAttribute('placeholder', 'dd/mm/yyyy')
      expect(screen.getByLabelText('To')).toHaveAttribute('placeholder', 'dd/mm/yyyy')
    })

    it('sends a typed dd/mm/yyyy date to the API as yyyy-MM-dd', async () => {
      const seen = recordFilters()
      const { user } = renderApp('/transactions')
      await screen.findByRole('list', { name: 'Transactions' })

      await user.type(screen.getByLabelText('From'), '5/1/2026')
      await user.tab()

      await waitFor(() => expect(seen).toContainEqual({ from: '2026-01-05', to: null }))
      expect(screen.getByLabelText('From')).toHaveValue('05/01/2026')
    })

    it('shows a date from the address as dd/mm/yyyy', async () => {
      renderApp('/transactions?from=2026-01-05&to=2026-02-13')
      await screen.findByRole('list', { name: 'Transactions' })

      expect(screen.getByLabelText('From')).toHaveValue('05/01/2026')
      expect(screen.getByLabelText('To')).toHaveValue('13/02/2026')
    })

    it('explains a date that does not exist and does not filter on it', async () => {
      const seen = recordFilters()
      const { user } = renderApp('/transactions')
      await screen.findByRole('list', { name: 'Transactions' })

      await user.type(screen.getByLabelText('From'), '31/02/2026')
      await user.tab()

      expect(screen.getByLabelText('From')).toHaveAttribute('aria-invalid', 'true')
      expect(screen.getByLabelText('From')).toHaveAccessibleDescription('Enter a date as dd/mm/yyyy.')
      expect(seen.every((s) => s.from === null)).toBe(true)
    })

    it('Clear empties both dates', async () => {
      const { user } = renderApp('/transactions?from=2026-01-05&to=2026-02-13')
      await screen.findByRole('list', { name: 'Transactions' })

      await user.click(screen.getByRole('button', { name: 'Clear' }))

      expect(screen.getByLabelText('From')).toHaveValue('')
      expect(screen.getByLabelText('To')).toHaveValue('')
    })
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

describe('TransactionsPage list states', () => {
  function recordRequests(body: unknown) {
    const searches: string[] = []
    server.use(
      http.get(api('/api/transactions'), ({ request }) => {
        searches.push(new URL(request.url).search)
        return HttpResponse.json(body as object)
      }),
    )
    return searches
  }

  it('says when the customer has no transactions at all', async () => {
    recordRequests(paged([]))
    renderApp('/transactions')

    expect(await screen.findByText('No transactions yet')).toBeInTheDocument()
    expect(screen.queryByText('Try a different search or date range.')).not.toBeInTheDocument()
  })

  it('says when filters match nothing, and suggests changing them', async () => {
    recordRequests(paged([]))
    renderApp('/transactions?search=nothing')

    expect(await screen.findByText('No transactions match your filters')).toBeInTheDocument()
    expect(screen.getByText('Try a different search or date range.')).toBeInTheDocument()
  })

  it('shows a failure and loads again on Try again', async () => {
    let calls = 0
    server.use(
      http.get(api('/api/transactions'), () => {
        calls++
        return calls === 1
          ? HttpResponse.json({ status: 404, detail: 'Could not load.', code: 'nope' }, { status: 404 })
          : HttpResponse.json(paged([eligibleTx]))
      }),
    )
    const { user } = renderApp('/transactions')

    expect(await screen.findByRole('alert')).toHaveTextContent('Could not load.')
    await user.click(screen.getByRole('button', { name: 'Try again' }))

    expect(await screen.findByRole('list', { name: 'Transactions' })).toBeInTheDocument()
  })

  it('moves between pages, keeping the filters', async () => {
    const searches = recordRequests({ items: [eligibleTx], page: 1, pageSize: 15, totalCount: 40, totalPages: 3 })
    const { user } = renderApp('/transactions?search=compu')

    await user.click(await screen.findByRole('button', { name: 'Next' }))

    await waitFor(() => expect(searches.at(-1)).toContain('page=2'))
    expect(searches.at(-1)).toContain('search=compu')
  })

  it('goes back to page 1 when a filter changes', async () => {
    const searches = recordRequests({ items: [eligibleTx], page: 2, pageSize: 15, totalCount: 40, totalPages: 3 })
    const { user } = renderApp('/transactions?page=2')
    await screen.findByRole('list', { name: 'Transactions' })

    await user.type(screen.getByLabelText('To'), '13/02/2026')
    await user.tab()

    await waitFor(() => expect(searches.at(-1)).toContain('2026-02-13'))
    expect(searches.at(-1)).not.toContain('page=2')
  })

  it('handles the search form itself, so submitting it never reloads the page', async () => {
    recordRequests(paged([eligibleTx]))
    renderApp('/transactions')
    await screen.findByRole('list', { name: 'Transactions' })

    // fireEvent returns false when the handler called preventDefault.
    expect(fireEvent.submit(screen.getByRole('search'))).toBe(false)
  })
})
