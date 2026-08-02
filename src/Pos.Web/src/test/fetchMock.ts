/**
 * A stubbable `fetch` for tests that go through the generated client.
 *
 * `vi.stubGlobal('fetch', …)` inside a test does **not** reach `api`/`deviceApi`:
 * `createClient` captures `globalThis.fetch` once, when `api/client.ts` is first
 * evaluated, which happens at import time — before any test body runs. A stub
 * installed later is simply never consulted, and the request goes to the real
 * network, where it fails with a `TypeError` that looks exactly like the offline
 * case a test may be trying to distinguish from something else.
 *
 * So this module installs one stable function up front and lets tests swap what
 * it *does*. **Import it before anything that imports the client** — ESM
 * evaluates a module's imports in declaration order, so it must be the first
 * import line in the test file.
 *
 * `refresh.ts` calls `fetch` at call time and is stubbable either way; this is
 * for everything that goes through `openapi-fetch`.
 */

type FetchHandler = (input: RequestInfo | URL, init?: RequestInit) => Promise<Response>

let handler: FetchHandler | null = null

globalThis.fetch = async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
  if (handler === null) {
    // Loudly. The alternative is a test quietly reaching the network and
    // failing later for a reason that has nothing to do with what it asserts.
    throw new Error('No fetch handler installed — call installFetchHandler() first.')
  }

  return handler(input, init)
}

export function installFetchHandler(fn: FetchHandler): void {
  handler = fn
}

export function resetFetchHandler(): void {
  handler = null
}

/** The URL of a request, however the caller expressed it. */
export function requestUrl(input: RequestInfo | URL): string {
  if (typeof input === 'string') {
    return input
  }

  return input instanceof URL ? input.href : input.url
}
