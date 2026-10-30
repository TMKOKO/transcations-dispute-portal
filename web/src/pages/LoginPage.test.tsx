import { screen } from '@testing-library/react'
import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'
import { renderApp } from '../test/render'
import { api, server } from '../test/server'

describe('LoginPage', () => {
  it('signs in and lands on transactions', async () => {
    server.use(
      http.post(api('/api/auth/login'), () =>
        HttpResponse.json({ displayName: 'Thandi', role: 'customer', idleTimeoutSeconds: 300 }),
      ),
    )
    const { user } = renderApp('/login', { signedIn: false })

    await user.type(screen.getByLabelText('Email address'), 'thandi@example.com')
    await user.type(screen.getByLabelText('Password'), 'Demo@12345!')
    await user.click(screen.getByRole('button', { name: 'Sign in' }))

    expect(await screen.findByRole('heading', { name: 'Transactions' })).toBeInTheDocument()
    expect(screen.getByText('Hi, Thandi')).toBeInTheDocument()
  })

  it('checks the fields before calling the API, and points at the first problem', async () => {
    let calls = 0
    server.use(
      http.post(api('/api/auth/login'), () => {
        calls++
        return HttpResponse.json({ displayName: 'Thandi', role: 'customer', idleTimeoutSeconds: 300 })
      }),
    )
    const { user } = renderApp('/login', { signedIn: false })

    await user.click(screen.getByRole('button', { name: 'Sign in' }))

    expect(screen.getByLabelText('Email address')).toHaveAccessibleDescription('Enter your email address.')
    expect(screen.getByLabelText('Password')).toHaveAccessibleDescription('Enter your password.')
    expect(screen.getByLabelText('Email address')).toHaveFocus()

    await user.type(screen.getByLabelText('Email address'), 'thandi@')
    await user.type(screen.getByLabelText('Password'), 'Demo@12345!')
    await user.click(screen.getByRole('button', { name: 'Sign in' }))

    expect(screen.getByLabelText('Email address')).toHaveAccessibleDescription('Enter a valid email address, like name@example.com.')
    expect(screen.getByLabelText('Email address')).toHaveAttribute('aria-invalid', 'true')
    expect(screen.getByLabelText('Password')).not.toHaveAttribute('aria-invalid')
    expect(calls).toBe(0)
  })

  it('shows the API message for bad credentials and stays on the page', async () => {
    server.use(
      http.post(api('/api/auth/login'), () =>
        HttpResponse.json({ status: 401, detail: 'Email or password is incorrect.', code: 'invalid_credentials' }, { status: 401 }),
      ),
    )
    const { user } = renderApp('/login', { signedIn: false })

    await user.type(screen.getByLabelText('Email address'), 'thandi@example.com')
    await user.type(screen.getByLabelText('Password'), 'wrong')
    await user.click(screen.getByRole('button', { name: 'Sign in' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Email or password is incorrect.')
    expect(screen.getByRole('heading', { name: 'Sign in' })).toBeInTheDocument()
  })

  it('signs out and explains why when the API rejects the session', async () => {
    server.use(http.get(api('/api/transactions'), () => new HttpResponse(null, { status: 401 })))
    renderApp('/transactions')

    expect(await screen.findByRole('heading', { name: 'Sign in' })).toBeInTheDocument()
    expect(screen.getByRole('status')).toHaveTextContent('Your session has ended')
  })

  it('signs out this tab only when another account has taken over the browser', async () => {
    let logoutCalls = 0
    server.use(
      http.get(api('/api/transactions'), () =>
        HttpResponse.json({ status: 403, detail: 'You do not have access to this.', code: 'forbidden' }, { status: 403 }),
      ),
      http.post(api('/api/auth/logout'), () => {
        logoutCalls++
        return new HttpResponse(null, { status: 204 })
      }),
    )
    renderApp('/transactions')

    expect(await screen.findByRole('heading', { name: 'Sign in' })).toBeInTheDocument()
    expect(screen.getByRole('status')).toHaveTextContent('Another account signed in on this browser')
    // The cookie is the other account's now; signing out here would end that session too.
    expect(logoutCalls).toBe(0)
  })
})
