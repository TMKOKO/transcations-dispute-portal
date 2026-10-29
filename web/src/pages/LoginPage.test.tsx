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
})
