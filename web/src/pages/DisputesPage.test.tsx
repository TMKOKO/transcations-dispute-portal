import { screen, waitFor, within } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'
import { paths } from '../lib/routes'
import { renderApp } from '../test/render'
import { api, disputeDetail, paged, server } from '../test/server'

/** Answers every disputes request with `body`, and records the query string of each. */
function recordRequests(body: unknown = paged([disputeDetail])) {
  const searches: string[] = []
  server.use(
    http.get(api('/api/disputes'), ({ request }) => {
      searches.push(new URL(request.url).search)
      return HttpResponse.json(body as object)
    }),
  )
  return searches
}

describe('DisputesPage', () => {
  it('lists each dispute and links to its detail page', async () => {
    renderApp(paths.disputes)

    const list = await screen.findByRole('list', { name: 'Disputes' })
    const link = within(list).getByRole('link', { name: /DSP-2026-001000/ })
    expect(link).toHaveAttribute('href', '/disputes/detail')
    expect(link).toHaveTextContent('Takealot.com')
    expect(link).toHaveTextContent(/R\s?850[.,]00/)
    expect(link).toHaveTextContent('Under review')
  })

  it('opens a dispute with its id in router state, not the address', async () => {
    let lookedUp: unknown
    server.use(
      http.post(api('/api/disputes/lookup'), async ({ request }) => {
        lookedUp = await request.json()
        return HttpResponse.json(disputeDetail)
      }),
    )
    const { user } = renderApp(paths.disputes)

    await user.click(await screen.findByRole('link', { name: /DSP-2026-001000/ }))

    expect(await screen.findByRole('heading', { name: 'DSP-2026-001000' })).toBeInTheDocument()
    expect(lookedUp).toEqual({ disputeId: 'd-1' })
  })

  it.each([
    ['Submitted', 'Submitted'],
    ['Under review', 'UnderReview'],
    ['Resolved', 'Resolved'],
    ['Declined', 'Rejected'],
    ['Withdrawn', 'Withdrawn'],
  ])('filters by %s, and All clears the filter', async (label, status) => {
    const searches = recordRequests()
    const { user } = renderApp(paths.disputes)
    await screen.findByRole('list', { name: 'Disputes' })
    const group = screen.getByRole('group', { name: 'Filter by status' })
    expect(within(group).getByRole('button', { name: 'All' })).toHaveAttribute('aria-pressed', 'true')

    await user.click(within(group).getByRole('button', { name: label }))

    await waitFor(() => expect(searches.at(-1)).toContain(`status=${status}`))
    expect(within(group).getByRole('button', { name: label })).toHaveAttribute('aria-pressed', 'true')
    expect(within(group).getByRole('button', { name: 'All' })).toHaveAttribute('aria-pressed', 'false')

    await user.click(within(group).getByRole('button', { name: 'All' }))
    expect(within(group).getByRole('button', { name: 'All' })).toHaveAttribute('aria-pressed', 'true')
    expect(within(group).getByRole('button', { name: label })).toHaveAttribute('aria-pressed', 'false')
  })

  it('points a customer with no disputes at their transactions', async () => {
    recordRequests(paged([]))
    renderApp(paths.disputes)

    expect(await screen.findByText("You haven't raised any disputes")).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'transactions' })).toHaveAttribute('href', '/transactions')
  })

  it('says when a filter matches nothing, without the transactions hint', async () => {
    recordRequests(paged([]))
    renderApp(`${paths.disputes}?status=UnderReview`)

    expect(await screen.findByText('No under review disputes')).toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'transactions' })).not.toBeInTheDocument()
  })

  it('shows a failure and loads again on Try again', async () => {
    let calls = 0
    server.use(
      http.get(api('/api/disputes'), () => {
        calls++
        return calls === 1
          ? HttpResponse.json({ status: 404, detail: 'Could not load.', code: 'nope' }, { status: 404 })
          : HttpResponse.json(paged([disputeDetail]))
      }),
    )
    const { user } = renderApp(paths.disputes)

    expect(await screen.findByRole('alert')).toHaveTextContent('Could not load.')
    await user.click(screen.getByRole('button', { name: 'Try again' }))

    expect(await screen.findByRole('list', { name: 'Disputes' })).toBeInTheDocument()
  })

  it('keeps the filter when moving between pages', async () => {
    const searches = recordRequests({ items: [disputeDetail], page: 1, pageSize: 15, totalCount: 40, totalPages: 3 })
    const { user } = renderApp(`${paths.disputes}?status=Resolved`)

    const pages = await screen.findByRole('navigation', { name: 'Pagination' })
    expect(pages).toHaveTextContent('Page 1 of 3 · 40 results')
    expect(within(pages).getByRole('button', { name: 'Previous' })).toBeDisabled()

    await user.click(within(pages).getByRole('button', { name: 'Next' }))

    await waitFor(() => expect(searches.at(-1)).toContain('page=2'))
    expect(searches.at(-1)).toContain('status=Resolved')
  })

  it('offers no pagination for a single page', async () => {
    renderApp(paths.disputes)

    await screen.findByRole('list', { name: 'Disputes' })
    expect(screen.queryByRole('navigation', { name: 'Pagination' })).not.toBeInTheDocument()
  })
})
