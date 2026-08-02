/**
 * Where the session's two tokens live.
 *
 * **Access token: memory only.** It is short-lived (~15 minutes, `ClockSkew =
 * TimeSpan.Zero` in `Program.cs`, so that is the real number rather than the
 * usual five minutes of grace) and never written anywhere a script can read it
 * back after a reload.
 *
 * **Refresh token: `sessionStorage`.** This is a deliberate trade-off, not an
 * oversight, and it is written up in DECISIONS.md:
 *
 * - The alternative is an `httpOnly` cookie, which the API does not currently
 *   issue — `POST /auth/login` returns both tokens in the JSON body. Adding one
 *   is a backend change whose right shape depends on the deployment topology,
 *   and Phase 8.2 puts the web app on a CDN and the API in a container, i.e.
 *   cross-origin, which needs `SameSite=None; Secure` plus credentialed CORS.
 *   Deciding that now would be guessing. Revisit it in 8.2.
 * - `sessionStorage` rather than `localStorage`: both are readable by an
 *   injected script, but `sessionStorage` dies with the tab, so a shared
 *   counter tablet does not keep a usable refresh token after the browser is
 *   closed. The cost is that a till has to log in again after a reboot, which
 *   is the correct direction to fail for a shared device.
 * - The mitigation that actually limits the damage is server-side and already
 *   built: refresh tokens rotate on every use and reuse of a rotated token
 *   revokes the whole family (`TokenService`). A stolen refresh token is
 *   therefore usable until its owner next refreshes, at which point the theft
 *   logs both parties out and is visible.
 *
 * The **device** token is not here. It is a device credential, must survive a
 * browser restart, and lives in `deviceToken.ts`.
 */

const REFRESH_TOKEN_KEY = 'pos.refreshToken'

/**
 * Refresh this long before the access token actually expires, so an in-flight
 * request never races the expiry it was issued under.
 */
const REFRESH_MARGIN_MS = 60_000

interface AccessToken {
  value: string
  /** Epoch milliseconds. */
  expiresAt: number
}

let accessToken: AccessToken | null = null

/** Notified when the session is cleared, so the UI can raise re-authentication. */
type ClearedListener = () => void
const clearedListeners = new Set<ClearedListener>()

export function getAccessToken(): string | null {
  return accessToken?.value ?? null
}

/**
 * Whether the access token is close enough to expiry to be worth replacing.
 *
 * True when there is no token at all, so a session restored from
 * `sessionStorage` on a page load refreshes rather than firing one doomed
 * request per query to discover the same thing.
 */
export function accessTokenNeedsRefresh(now: number = Date.now()): boolean {
  return accessToken === null || accessToken.expiresAt - REFRESH_MARGIN_MS <= now
}

/** Epoch ms at which the current access token expires, or `null`. */
export function accessTokenExpiresAt(): number | null {
  return accessToken?.expiresAt ?? null
}

export function getRefreshToken(): string | null {
  try {
    return sessionStorage.getItem(REFRESH_TOKEN_KEY)
  } catch {
    // Safari in private mode, and any embedded webview with storage disabled.
    // A session that cannot be persisted still works until the tab is reloaded,
    // which is better than a blank screen.
    return null
  }
}

/**
 * Records the pair returned by `/auth/login`, `/auth/pin` or `/auth/refresh`.
 *
 * `expiresIn` is the API's `ExpiresIn`, in seconds.
 */
export function setTokens(tokens: {
  accessToken: string
  refreshToken: string
  expiresIn: number
}): void {
  accessToken = {
    value: tokens.accessToken,
    expiresAt: Date.now() + tokens.expiresIn * 1000,
  }

  try {
    sessionStorage.setItem(REFRESH_TOKEN_KEY, tokens.refreshToken)
  } catch {
    // See getRefreshToken. The in-memory access token still works.
  }
}

/**
 * Drops the session.
 *
 * Called on logout and whenever a refresh fails. Listeners fire *after* the
 * tokens are gone, so anything they trigger cannot pick up a stale one.
 */
export function clearTokens(): void {
  accessToken = null

  try {
    sessionStorage.removeItem(REFRESH_TOKEN_KEY)
  } catch {
    // Nothing to do — there is no token to leak if it could not be written.
  }

  for (const listener of clearedListeners) {
    listener()
  }
}

/** Subscribe to session loss. Returns an unsubscribe function. */
export function onTokensCleared(listener: ClearedListener): () => void {
  clearedListeners.add(listener)
  return () => {
    clearedListeners.delete(listener)
  }
}

/** Test seam. Not used by application code. */
export function __resetTokenStore(): void {
  accessToken = null
  clearedListeners.clear()
  try {
    sessionStorage.removeItem(REFRESH_TOKEN_KEY)
  } catch {
    // Ignored: see clearTokens.
  }
}
