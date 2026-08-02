import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { __resetRefresh, refreshSession } from './refresh'
import {
  __resetTokenStore,
  getAccessToken,
  getRefreshToken,
  onTokensCleared,
  setTokens,
} from './tokenStore'

/**
 * The single-flight refresh.
 *
 * This is the test the phase doc names by itself, and the reason is specific:
 * refresh tokens **rotate on every use**, and `TokenService` treats reuse of an
 * already-rotated token as evidence of a leak and revokes the entire family.
 *
 * So the failure mode of a naive implementation is not "an extra request". It
 * is: five queries 401 at once, five refreshes start, the first rotates
 * successfully and the other four present a token that is now spent, the server
 * concludes it has been stolen, and the cashier is logged out in the middle of
 * a sale. That is a bug you would never reproduce by clicking around, because
 * it needs concurrency.
 */
describe('refreshSession', () => {
  let fetchMock: ReturnType<typeof vi.fn>

  beforeEach(() => {
    __resetTokenStore()
    __resetRefresh()
    fetchMock = vi.fn()
    vi.stubGlobal('fetch', fetchMock)
  })

  afterEach(() => {
    vi.unstubAllGlobals()
  })

  /** A `/auth/refresh` that does not resolve until told to. */
  function deferredSuccess() {
    let release!: () => void
    const gate = new Promise<void>((resolve) => {
      release = resolve
    })

    fetchMock.mockImplementation(async () => {
      await gate
      return new Response(
        JSON.stringify({
          accessToken: 'new-access',
          refreshToken: 'rotated-refresh',
          expiresIn: 900,
          user: { id: 'u', displayName: 'Ada', role: 'Owner', policies: [] },
        }),
        { status: 200, headers: { 'Content-Type': 'application/json' } },
      )
    })

    return release
  }

  it('makes exactly one request for five concurrent callers', async () => {
    setTokens({ accessToken: 'old', refreshToken: 'original-refresh', expiresIn: 900 })

    // Held open so all five callers are genuinely in flight together. Firing
    // them at a mock that resolves immediately would let each one finish before
    // the next began, and the test would pass against a broken implementation.
    const release = deferredSuccess()

    const callers = [
      refreshSession(),
      refreshSession(),
      refreshSession(),
      refreshSession(),
      refreshSession(),
    ]

    release()

    const results = await Promise.all(callers)

    expect(fetchMock).toHaveBeenCalledTimes(1)
    expect(results).toEqual([true, true, true, true, true])
  })

  it('sends the stored refresh token and adopts the rotated pair', async () => {
    setTokens({ accessToken: 'old', refreshToken: 'original-refresh', expiresIn: 900 })

    const release = deferredSuccess()
    const pending = refreshSession()
    release()

    await expect(pending).resolves.toBe(true)

    const [, init] = fetchMock.mock.calls[0] as [string, RequestInit]
    expect(JSON.parse(String(init.body))).toEqual({ refreshToken: 'original-refresh' })

    // Rotation: the old token must not survive, or the next refresh replays a
    // spent one and revokes the family.
    expect(getAccessToken()).toBe('new-access')
    expect(getRefreshToken()).toBe('rotated-refresh')
  })

  it('starts a fresh flight once the previous one has settled', async () => {
    setTokens({ accessToken: 'old', refreshToken: 'original-refresh', expiresIn: 900 })

    const release = deferredSuccess()
    const first = refreshSession()
    release()
    await first

    const releaseAgain = deferredSuccess()
    const second = refreshSession()
    releaseAgain()
    await second

    // The in-flight promise is cleared in a `finally`, so the app is not wedged
    // into never refreshing again after the first one completes.
    expect(fetchMock).toHaveBeenCalledTimes(2)
  })

  it('clears the session and notifies when the token is rejected', async () => {
    setTokens({ accessToken: 'old', refreshToken: 'stale-refresh', expiresIn: 900 })

    const cleared = vi.fn()
    onTokensCleared(cleared)

    fetchMock.mockResolvedValue(new Response('{}', { status: 401 }))

    await expect(refreshSession()).resolves.toBe(false)

    expect(getAccessToken()).toBeNull()
    expect(getRefreshToken()).toBeNull()
    // This is what raises the re-auth prompt.
    expect(cleared).toHaveBeenCalledTimes(1)
  })

  it('keeps the token when the network is down rather than the token being bad', async () => {
    setTokens({ accessToken: 'old', refreshToken: 'good-refresh', expiresIn: 900 })

    const cleared = vi.fn()
    onTokensCleared(cleared)

    fetchMock.mockRejectedValue(new TypeError('Failed to fetch'))

    await expect(refreshSession()).resolves.toBe(false)

    // A shop's broadband dropping for two seconds must not sign the till out.
    // The token is still valid; the next attempt will use it.
    expect(getRefreshToken()).toBe('good-refresh')
    expect(cleared).not.toHaveBeenCalled()
  })

  it('does not call the API when there is nothing to rotate', async () => {
    const cleared = vi.fn()
    onTokensCleared(cleared)

    await expect(refreshSession()).resolves.toBe(false)

    expect(fetchMock).not.toHaveBeenCalled()
    // Still notifies, or a caller waits for a prompt that never appears.
    expect(cleared).toHaveBeenCalledTimes(1)
  })

  it('clears the session when the response is not the shape it claims', async () => {
    setTokens({ accessToken: 'old', refreshToken: 'original-refresh', expiresIn: 900 })

    // A 200 carrying something else — a proxy's login page, most realistically.
    // Adopting it would store `undefined` as the access token and every
    // subsequent request would fail with no explanation.
    fetchMock.mockResolvedValue(
      new Response(JSON.stringify({ accessToken: 'a' }), {
        status: 200,
        headers: { 'Content-Type': 'application/json' },
      }),
    )

    await expect(refreshSession()).resolves.toBe(false)
    expect(getAccessToken()).toBeNull()
  })
})
