import { render } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, type InitialEntry } from 'react-router'
import { App } from '../App'
import { createQueryClient } from '../api/queryClient'
import type { Role } from '../api/types'

/** Render the whole app at `route` (a path, or a path plus router state), optionally already signed in. */
export function renderApp(route: InitialEntry, { signedIn = true, role = 'customer' as Role } = {}) {
  if (signedIn) {
    sessionStorage.setItem(
      'dispute-portal.session',
      JSON.stringify({ displayName: 'Thandi', idleTimeoutSeconds: 300, role, idleExpiresAt: Date.now() + 300_000 }),
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
