// Must be first: it installs the `fetch` that `api/client.ts` captures at
// import time. See the module's own comment.
import { installFetchHandler, requestUrl, resetFetchHandler } from '@/test/fetchMock'
import { render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { AuthProvider } from './AuthProvider'
import { useAuth } from './authContext'
import { __resetRefresh } from './refresh'
import { __resetTokenStore, getRefreshToken, setTokens } from './tokenStore'

/**
 * Restoring a session on page load.
 *
 * The distinction under test is the one `refresh.ts` already makes and this
 * layer did not: **a dropped connection is not a rejected credential.** A till
 * on a shop's flaky broadband reloads, every request fails at the transport,
 * and a provider that reads that as "your token is no good" throws away a
 * perfectly valid refresh token and lands the cashier on a login screen with a
 * cart behind it. The re-auth overlay exists precisely so that cannot happen,
 * and this path used to route around it.
 */
describe('AuthProvider session restore', () => {
  beforeEach(() => {
    __resetTokenStore()
    __resetRefresh()
  })

  afterEach(() => {
    resetFetchHandler()
    vi.unstubAllGlobals()
  })

  /** A rotation that works, so `/auth/me` is the only thing under test. */
  function refreshSucceeds(): Response {
    return new Response(
      JSON.stringify({ accessToken: 'fresh', refreshToken: 'rotated', expiresIn: 900 }),
      { status: 200, headers: { 'Content-Type': 'application/json' } },
    )
  }

  /** Reports the session status, which is the whole observable behaviour here. */
  function Probe() {
    const { status } = useAuth()
    return <p data-testid="status">{status}</p>
  }

  function renderProvider() {
    // `retry: false` so a failure is observed once rather than after the app
    // client's two backoff attempts. The retry policy itself is queryClient's.
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })

    return render(
      <QueryClientProvider client={client}>
        <AuthProvider>
          <Probe />
        </AuthProvider>
      </QueryClientProvider>,
    )
  }

  it('keeps the refresh token when nothing answers at all', async () => {
    setTokens({ accessToken: 'old', refreshToken: 'good-refresh', expiresIn: 900 })

    // Everything fails at the transport: the API is down, or the shop's
    // connection dropped. Nothing here rejected the credential.
    const handler = vi.fn().mockRejectedValue(new TypeError('Failed to fetch'))
    installFetchHandler(handler)

    renderProvider()

    await waitFor(
      () => {
        expect(screen.getByTestId('status')).toHaveTextContent('unreachable')
      },
      // Past testing-library's 1s default: this waits on a fetch, a React
      // effect and a re-render, and a loaded CI runner has been seen to take
      // longer than a second over that.
      { timeout: 5000 },
    )

    // The point of the whole test. Signing out here loses the cart.
    expect(getRefreshToken()).toBe('good-refresh')
  })

  it('keeps the refresh token when the server is broken', async () => {
    setTokens({ accessToken: 'old', refreshToken: 'good-refresh', expiresIn: 900 })

    installFetchHandler(async (input) => {
      if (requestUrl(input).includes('/auth/refresh')) {
        return refreshSucceeds()
      }

      // A 500 is the API having a bad day, not this session being over.
      return new Response(JSON.stringify({ title: 'Server error' }), {
        status: 500,
        headers: { 'Content-Type': 'application/problem+json' },
      })
    })

    renderProvider()

    await waitFor(
      () => {
        expect(screen.getByTestId('status')).toHaveTextContent('unreachable')
      },
      // Past testing-library's 1s default: this waits on a fetch, a React
      // effect and a re-render, and a loaded CI runner has been seen to take
      // longer than a second over that.
      { timeout: 5000 },
    )

    // Untouched: a 500 does not trigger a rotation, and must not drop the token.
    expect(getRefreshToken()).toBe('good-refresh')
  })

  it('ends the session when the credential is actually rejected', async () => {
    setTokens({ accessToken: 'old', refreshToken: 'stale-refresh', expiresIn: 900 })

    installFetchHandler(async (input) => {
      if (requestUrl(input).includes('/auth/refresh')) {
        return refreshSucceeds()
      }

      // The rotation worked and `/auth/me` still says no: whatever the reason,
      // this token is not usable and that session is over.
      return new Response(JSON.stringify({ title: 'Unauthorized' }), {
        status: 401,
        headers: { 'Content-Type': 'application/problem+json' },
      })
    })

    renderProvider()

    await waitFor(
      () => {
        expect(screen.getByTestId('status')).toHaveTextContent('anonymous')
      },
      { timeout: 5000 },
    )

    expect(getRefreshToken()).toBeNull()
  })

  it('does not ask the API anything when there is no session to restore', () => {
    const handler = vi.fn()
    installFetchHandler(handler)

    renderProvider()

    expect(screen.getByTestId('status')).toHaveTextContent('anonymous')
    expect(handler).not.toHaveBeenCalled()
  })
})
