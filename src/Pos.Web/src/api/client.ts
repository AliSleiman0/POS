/**
 * The typed API client.
 *
 * Types come from `schema.d.ts`, which `pnpm generate:api` produces from the
 * running API's OpenAPI document. **Hand-written request/response interfaces
 * are forbidden** (CLAUDE.md, ARCHITECTURE.md): they drift silently, and a
 * renamed field compiles fine and produces `undefined` at runtime — which in a
 * money context is a blank total on a receipt.
 *
 * The generated paths already carry the `/api/v1` prefix, so `baseUrl` is just
 * the origin — see `baseUrl.ts` for where that origin comes from. In
 * development Vite proxies `/api` to `http://localhost:5013`, so the browser
 * sees one origin and there is no CORS and no cross-site cookie question; a
 * deployed build points at the API's own host and CORS applies.
 */

import createClient, { type Middleware } from 'openapi-fetch'
import type { paths } from './schema'
import { API_BASE_URL } from './baseUrl'
import { ProblemError, toProblem } from './problem'
import { getDeviceToken } from '@/auth/deviceToken'
import { refreshSession } from '@/auth/refresh'
import { accessTokenNeedsRefresh, getAccessToken, getRefreshToken } from '@/auth/tokenStore'

/**
 * Endpoints that must not carry an `Authorization` header or trigger a refresh.
 *
 * `/auth/login` and `/auth/refresh` are `AllowAnonymous`; attaching a stale
 * token to them is harmless but proactively refreshing before them is not — on
 * the login page there is no session, and a refresh attempt would clear the
 * store and raise a "session expired" prompt over a login form.
 */
const ANONYMOUS_PATHS = ['/api/v1/auth/login', '/api/v1/auth/refresh']

function isAnonymous(url: string): boolean {
  // Resolved against the API's base, not the page's. The two are the same
  // origin in development and different in a deployed build, and reading a
  // request's path off the *page's* origin only ever happened to work.
  return ANONYMOUS_PATHS.some((path) => new URL(url, API_BASE_URL).pathname === path)
}

/**
 * Requests captured before they were sent, so a 401 can be retried.
 *
 * A `Request` whose body has been read cannot be sent again, so the clone has
 * to be taken in `onRequest`. Keyed by openapi-fetch's per-call `id`, and
 * removed in both `onResponse` and `onError` so a failed request does not leak
 * its clone.
 */
const retryables = new Map<string, Request>()

const authMiddleware: Middleware = {
  async onRequest({ request, id }) {
    if (isAnonymous(request.url)) {
      return request
    }

    // Pre-emptive: refresh a token that is about to expire rather than letting
    // the request fail and pay a second round trip. Guarded on a refresh token
    // actually existing, so an unauthenticated app does not spin.
    if (accessTokenNeedsRefresh() && getRefreshToken() !== null) {
      await refreshSession()
    }

    const token = getAccessToken()

    if (token !== null) {
      request.headers.set('Authorization', `Bearer ${token}`)
    }

    retryables.set(id, request.clone())
    return request
  },

  async onResponse({ response, id }) {
    const retryable = retryables.get(id)
    retryables.delete(id)

    if (response.status !== 401 || retryable === undefined) {
      return response
    }

    // Exactly one retry: the replay below uses `fetch` directly, so it does not
    // re-enter this middleware and cannot loop. `refreshSession` is
    // single-flight, so a burst of concurrent 401s produces one rotation.
    if (!(await refreshSession())) {
      return response
    }

    const token = getAccessToken()

    if (token === null) {
      return response
    }

    retryable.headers.set('Authorization', `Bearer ${token}`)
    return fetch(retryable)
  },

  async onError({ id }) {
    retryables.delete(id)
  },
}

/**
 * The authenticated client. Every call goes through `unwrap`.
 */
export const api = createClient<paths>({ baseUrl: API_BASE_URL })

api.use(authMiddleware)

/**
 * A second client for the two endpoints authenticated by the **DeviceToken
 * scheme** rather than a bearer token: `GET /employees/pin-eligible` and
 * `POST /auth/pin`.
 *
 * Separate rather than a flag, because the `EnrolledDevice` policy names the
 * scheme explicitly (`Program.cs`) — sending an `Authorization` header instead
 * would be answered by the JWT scheme, and the second factor would quietly
 * disappear. Keeping them on a client that *cannot* attach a bearer token means
 * that mistake is not available.
 */
export const deviceApi = createClient<paths>({ baseUrl: API_BASE_URL })

deviceApi.use({
  onRequest({ request }) {
    const token = getDeviceToken()

    if (token !== null) {
      request.headers.set('X-Device-Token', token)
    }

    return request
  },
})

/** What openapi-fetch resolves to for one call. */
interface FetchResult<T> {
  data?: T
  error?: unknown
  response: Response
}

/**
 * Turns openapi-fetch's `{ data, error }` result into a value or a throw.
 *
 * TanStack Query decides success or failure by whether the query function
 * threw, so a client that returned `{ error }` would report every failure as a
 * successful fetch of `undefined`. At a till that is CLAUDE.md invariant 10's
 * sibling problem: a failed API call must never be a silent no-op.
 *
 * @throws {ProblemError} on any non-2xx.
 */
export async function unwrap<T>(call: Promise<FetchResult<T>>): Promise<T> {
  const { data, error, response } = await call

  if (!response.ok) {
    throw new ProblemError(response.status, toProblem(response.status, error, response.statusText))
  }

  // 204 and friends: `data` is legitimately undefined and the call type says so.
  return data as T
}

/**
 * `unwrap`, keeping the `Response`.
 *
 * For the one caller that needs a header off it: `POST /sales` sets
 * `Idempotent-Replay: true` when a key has been seen before, and "that sale was
 * already recorded" is a different thing to tell a cashier than "sale recorded"
 * — after a timeout and a retry it is the true one. See `wasReplayed` in
 * `api/idempotency.ts`.
 *
 * Deliberately a sibling rather than a change to `unwrap`: almost nothing wants
 * the response, and a tuple return everywhere would be ceremony at ninety call
 * sites to serve one.
 *
 * @throws {ProblemError} on any non-2xx, by the same path as `unwrap`.
 */
export async function unwrapWithResponse<T>(
  call: Promise<FetchResult<T>>,
): Promise<{ data: T; response: Response }> {
  const { data, error, response } = await call

  if (!response.ok) {
    throw new ProblemError(response.status, toProblem(response.status, error, response.statusText))
  }

  return { data: data as T, response }
}

/** Test seam. Not used by application code. */
export function __clearRetryables(): void {
  retryables.clear()
}
