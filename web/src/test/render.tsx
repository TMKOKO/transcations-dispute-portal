import { render } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, type InitialEntry } from 'react-router'
import { App } from '../App'
import { createQueryClient } from '../api/queryClient'

/** Render the whole app at `route` (a path, or a path plus router state), optionally already signed in. */
export function renderApp(route: InitialEntry, { signedIn = true } = {}) {
  if (signedIn) {
    sessionStorage.setItem(
      'dispute-portal.session',
      JSON.stringify({ token: 'test-token', expiresAt: new Date(Date.now() + 30 * 60_000).toISOString(), displayName: 'Thandi' }),
    )
  }

  const queryClient = createQueryClient()
  queryClient.setDefaultOptions({ queries: { ...queryClient.getDefaultOptions().queries, retry: false } })

  const user = userEvent.setup()
  const utils = render(
    <MemoryRouter initialEntries={[route]}>
      <App queryClient={queryClient} />
    </MemoryRouter>,
  )
  return { user, queryClient, ...utils }
}
