import { http, HttpResponse } from 'msw'
import { describe, expect, it, vi } from 'vitest'
import { api, server } from '../test/server'
import { ApiError, configureAuth, messageFromProblem, request } from './client'

describe('messageFromProblem', () => {
  it('prefers detail', () => {
    expect(messageFromProblem({ title: 'Conflict', detail: 'Already open.' }, 409)).toBe('Already open.')
  })

  it('flattens model-validation errors instead of showing the generic title', () => {
    const body = {
      title: 'One or more validation errors occurred.',
      errors: { description: ['Too short.'], disputedAmount: ['Must be positive.'] },
    }
    expect(messageFromProblem(body, 400)).toBe('Too short. Must be positive.')
  })

  it('falls back to title, then a generic message', () => {
    expect(messageFromProblem({ title: 'Not Found' }, 404)).toBe('Not Found')
    expect(messageFromProblem(null, 500)).toMatch(/something went wrong/i)
    expect(messageFromProblem(null, 0)).toMatch(/could not reach the server/i)
  })
})

describe('request', () => {
  it('sends the session cookie, never an Authorization header, and returns parsed JSON', async () => {
    let auth: string | null = 'unset'
    server.use(
      http.get(api('/api/me'), ({ request: req }) => {
        auth = req.headers.get('Authorization')
        return HttpResponse.json({ id: '1' })
      }),
    )
    const fetchSpy = vi.spyOn(globalThis, 'fetch')

    const res = await request<{ id: string }>('/api/me')

    expect(fetchSpy).toHaveBeenCalledWith(expect.any(String), expect.objectContaining({ credentials: 'include' }))
    expect(auth).toBeNull()
    expect(res.data.id).toBe('1')
    fetchSpy.mockRestore()
  })

  it('reports activity for answered requests, but not for 401s or the sign-in call', async () => {
    const onActivity = vi.fn()
    configureAuth(() => {}, onActivity)
    server.use(
      http.get(api('/api/me'), () => HttpResponse.json({ id: '1' })),
      http.post(api('/api/disputes'), () => HttpResponse.json({ status: 422, code: 'transaction_pending' }, { status: 422 })),
      http.get(api('/api/transactions'), () => new HttpResponse(null, { status: 401 })),
      http.post(api('/api/auth/login'), () => HttpResponse.json({ displayName: 'T', role: 'customer', idleTimeoutSeconds: 300 })),
    )

    await request('/api/me')
    // A business-rule failure still went through the session and restarted its timer.
    await request('/api/disputes', { method: 'POST', body: {} }).catch(() => {})
    expect(onActivity).toHaveBeenCalledTimes(2)

    await request('/api/transactions').catch(() => {})
    await request('/api/auth/login', { method: 'POST', body: {}, skipAuthRedirect: true })
    expect(onActivity).toHaveBeenCalledTimes(2)
  })

  it('throws ApiError carrying status, code and field errors', async () => {
    server.use(
      http.post(api('/api/disputes'), () =>
        HttpResponse.json({ status: 409, detail: 'Already open.', code: 'dispute_already_open' }, { status: 409 }),
      ),
    )

    const err = await request('/api/disputes', { method: 'POST', body: {} }).catch((e: unknown) => e)

    expect(err).toBeInstanceOf(ApiError)
    expect(err).toMatchObject({ status: 409, code: 'dispute_already_open', message: 'Already open.' })
  })

  it('signs out on 401, except for login itself', async () => {
    const onUnauthorized = vi.fn()
    configureAuth(onUnauthorized)
    server.use(
      http.get(api('/api/me'), () => new HttpResponse(null, { status: 401 })),
      http.post(api('/api/auth/login'), () => HttpResponse.json({ status: 401, detail: 'Email or password is incorrect.' }, { status: 401 })),
    )

    await expect(request('/api/me')).rejects.toBeInstanceOf(ApiError)
    expect(onUnauthorized).toHaveBeenCalledTimes(1)

    await expect(request('/api/auth/login', { method: 'POST', body: {}, skipAuthRedirect: true })).rejects.toThrow(
      'Email or password is incorrect.',
    )
    expect(onUnauthorized).toHaveBeenCalledTimes(1)
  })

  it('reports network failures as a readable error', async () => {
    server.use(http.get(api('/api/me'), () => HttpResponse.error()))
    await expect(request('/api/me')).rejects.toMatchObject({ status: 0, code: 'network_error' })
  })
})
