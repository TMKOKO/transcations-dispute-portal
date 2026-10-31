import { describe, expect, it } from 'vitest'
import { ApiError } from './client'
import { createQueryClient } from './queryClient'

const retry = createQueryClient().getDefaultOptions().queries!.retry as (failures: number, error: Error) => boolean

describe('createQueryClient', () => {
  it.each([400, 401, 403, 404, 409, 422, 429])('does not retry a %i, which would only repeat the answer', (status) => {
    expect(retry(0, new ApiError(status, 'no'))).toBe(false)
  })

  it.each([
    ['a network failure', new ApiError(0, 'offline', 'network_error')],
    ['a 500', new ApiError(500, 'broken')],
    ['a 503', new ApiError(503, 'unavailable')],
    ['an unexpected error', new TypeError('bad')],
  ])('retries %s once, and only once', (_name, error) => {
    expect(retry(0, error)).toBe(true)
    expect(retry(1, error)).toBe(false)
  })

  it('never lets focus or reconnect cause a request, because any request keeps the session alive', () => {
    const queries = createQueryClient().getDefaultOptions().queries!
    expect(queries.refetchOnWindowFocus).toBe(false)
    expect(queries.refetchOnReconnect).toBe(false)
  })

  it('never retries writes on its own', () => {
    expect(createQueryClient().getDefaultOptions().mutations!.retry).toBe(false)
  })
})
