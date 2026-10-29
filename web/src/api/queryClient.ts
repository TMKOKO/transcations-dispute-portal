import { QueryClient } from '@tanstack/react-query'
import { ApiError } from './client'

export function createQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: {
        staleTime: 30_000,
        // Every API call restarts the session's 5-minute idle timer, so only the customer may
        // cause one. Refetching on focus or reconnect would keep an idle session alive for ever.
        refetchOnWindowFocus: false,
        refetchOnReconnect: false,
        // Retrying a 4xx only repeats the same answer; retry network blips and 5xx once.
        retry: (failureCount, error) =>
          failureCount < 1 && !(error instanceof ApiError && error.status >= 400 && error.status < 500),
      },
      // Never auto-retry writes: the dispute submit relies on the user (and its idempotency key).
      mutations: { retry: false },
    },
  })
}
