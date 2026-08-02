/**
 * Token refresh, shared by every caller that needs one.
 *
 * **The single-flight property is load-bearing, not an optimisation.** Refresh
 * tokens rotate on every use, and `TokenService` treats reuse of an
 * already-rotated token as evidence of a leak and revokes the entire family. So
 * five requests that all get a 401 at the same moment must not each POST the
 * same refresh token: the first rotation succeeds, the other four present a
 * spent token, and the server correctly concludes it has been stolen and logs
 * the cashier out mid-sale.
 *
 * One promise, therefore, shared by all of them.
 */

import { clearTokens, getRefreshToken, setTokens } from './tokenStore'

/** Shape of `AuthResponse`. Not imported from the generated schema: this module
 * is below the generated client in the dependency order — the client's 401
 * middleware calls into here, so importing the client back would be a cycle.
 * Only the three fields this function needs are read, and a Vitest test pins
 * them against the real response body. */
interface RefreshResult {
  accessToken: string
  refreshToken: string
  expiresIn: number
}

/**
 * The one in-flight refresh, or `null` when none is running.
 *
 * Module-level rather than per-client so a second client instance (the
 * device-token one, for PIN login) cannot start a competing rotation.
 */
let inFlight: Promise<boolean> | null = null

/**
 * Refreshes the session, or joins the refresh already running.
 *
 * Resolves `true` when there is a usable access token afterwards, `false` when
 * the session is gone — in which case the tokens have been cleared and
 * `onTokensCleared` listeners have fired, which is what raises the re-auth
 * prompt.
 *
 * Never rejects. Callers are error paths already; a refresh that threw would
 * replace a recoverable 401 with an unhandled rejection.
 */
export function refreshSession(): Promise<boolean> {
  // The whole mechanism. A concurrent caller gets the promise that is already
  // running, so exactly one POST /auth/refresh happens per rotation.
  inFlight ??= runRefresh().finally(() => {
    // Cleared in `finally` so a failed refresh does not wedge the app into
    // never refreshing again — the next 401 after a re-login starts a new one.
    inFlight = null
  })

  return inFlight
}

async function runRefresh(): Promise<boolean> {
  const refreshToken = getRefreshToken()

  if (refreshToken === null) {
    // Nothing to rotate. Still clear, so the "session ended" listeners fire and
    // the caller is not left waiting for a prompt that never appears.
    clearTokens()
    return false
  }

  let response: Response

  try {
    // Plain `fetch`, deliberately. Going through the generated client would run
    // this request through the 401 middleware that called us.
    response = await fetch('/api/v1/auth/refresh', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ refreshToken }),
    })
  } catch {
    // The network is down rather than the token being bad. Do NOT clear: the
    // token is still valid and the till may be on a flaky shop connection.
    // The caller surfaces the original failure and the next attempt retries.
    return false
  }

  if (!response.ok) {
    // 401 here means the token was rejected — expired, revoked, or its family
    // was invalidated by a reuse. That session is over.
    clearTokens()
    return false
  }

  let body: RefreshResult

  try {
    body = (await response.json()) as RefreshResult
  } catch {
    clearTokens()
    return false
  }

  if (
    typeof body.accessToken !== 'string' ||
    typeof body.refreshToken !== 'string' ||
    typeof body.expiresIn !== 'number'
  ) {
    clearTokens()
    return false
  }

  setTokens(body)
  return true
}

/** Test seam. Not used by application code. */
export function __resetRefresh(): void {
  inFlight = null
}
