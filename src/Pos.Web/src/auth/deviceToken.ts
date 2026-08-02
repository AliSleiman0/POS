/**
 * The register's device token.
 *
 * Different from the session tokens in `tokenStore.ts`, and stored differently
 * on purpose. This one identifies the **till**, not the person: it is issued
 * once by `POST /registers/{id}/enroll` (shown exactly once — the server keeps
 * only a SHA-256 digest) and it is what makes PIN login safe, because a 4-digit
 * secret is otherwise trivially brute-forced. See ARCHITECTURE.md.
 *
 * So it lives in `localStorage`: a shop tablet is rebooted, and a till that
 * needed re-enrolling by an Owner every morning would not be used. That is the
 * intended lifetime for a device credential and not the same trade-off as the
 * refresh token, which deliberately dies with the tab.
 *
 * Losing the device is handled server-side: `POST /registers/{id}/revoke`.
 */

const DEVICE_TOKEN_KEY = 'pos.deviceToken'

/**
 * The register the token belongs to, stored alongside it.
 *
 * `POST /registers/{id}/enroll` returns both, and the id is needed by
 * `GET /shifts/current?registerId=` — which is how a till knows whether its own
 * drawer is open. The alternative would be reading `register_id` off the access
 * token, but that claim is only present after a *PIN* login, so a manager
 * signed in by password at the same till would see no drawer at all.
 */
const REGISTER_ID_KEY = 'pos.registerId'

function read(key: string): string | null {
  try {
    return localStorage.getItem(key)
  } catch {
    return null
  }
}

export function getDeviceToken(): string | null {
  return read(DEVICE_TOKEN_KEY)
}

export function getEnrolledRegisterId(): string | null {
  return read(REGISTER_ID_KEY)
}

export function isDeviceEnrolled(): boolean {
  return getDeviceToken() !== null
}

export function setDeviceToken(token: string, registerId: string): void {
  try {
    localStorage.setItem(DEVICE_TOKEN_KEY, token)
    localStorage.setItem(REGISTER_ID_KEY, registerId)
  } catch {
    // Storage disabled. The till works as an ordinary browser session; PIN
    // login is unavailable, which the PIN screen reports rather than failing
    // with an unexplained 401.
  }
}

export function clearDeviceToken(): void {
  try {
    localStorage.removeItem(DEVICE_TOKEN_KEY)
    localStorage.removeItem(REGISTER_ID_KEY)
  } catch {
    // Nothing to do.
  }
}
