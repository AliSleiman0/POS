/**
 * Whether the server can actually be reached.
 *
 * **`navigator.onLine` is not that question and must not be treated as it.** It
 * reports whether the device has *a* network interface up — so a till on a shop
 * Wi-Fi whose router has lost its uplink reads `true`, and a captive portal
 * reads `true` while answering every request with a login page. Both are the
 * cases that matter: the till is online in the only sense the browser can see
 * and offline in the only sense a customer notices.
 *
 * So `navigator.onLine` is used for what it is good at — an instant, free signal
 * that something *changed* — and the answer comes from probing the API's own
 * readiness endpoint, which is the same thing `AppLayout`'s footer indicator has
 * been doing since Phase 0.5.
 *
 * One shared signal rather than a probe per caller: the register, the sync
 * driver and the outbox all want the same answer, and three timers asking three
 * times would be three times the traffic from an idle till.
 */

import { API_BASE_URL } from '@/api/baseUrl'

export type Connectivity = 'online' | 'offline' | 'unknown'

type Listener = (state: Connectivity) => void

const listeners = new Set<Listener>()

let current: Connectivity = 'unknown'
let probing: Promise<Connectivity> | null = null
let timer: ReturnType<typeof setInterval> | null = null

/**
 * How often an idle till re-checks.
 *
 * Thirty seconds is a compromise with one side that matters: a till that has
 * been offline needs to notice the network came back *soon*, because the
 * cashier is waiting to see their queue drain. Faster than this and an idle
 * shop generates constant traffic for nothing.
 */
const POLL_MS = 30_000

/**
 * Long enough for a slow shop connection, short enough that a cashier is not
 * left looking at a stale indicator.
 *
 * **The lossy middle is what this bounds.** Fully offline fails instantly;
 * what a real shop has is a connection that answers in nine seconds, and a
 * probe with no timeout would report "online" long after the till had given up
 * on everything else.
 */
const PROBE_TIMEOUT_MS = 5_000

export function getConnectivity(): Connectivity {
  return current
}

export function isOffline(): boolean {
  return current === 'offline'
}

/** Subscribe. Returns an unsubscribe function. */
export function onConnectivityChange(listener: Listener): () => void {
  listeners.add(listener)
  return () => {
    listeners.delete(listener)
  }
}

/**
 * Starts watching. Idempotent, so every component may call it.
 *
 * @returns a function that stops the watch when the last caller is gone.
 */
export function startConnectivityWatch(): () => void {
  if (timer === null) {
    timer = setInterval(() => {
      void probe()
    }, POLL_MS)

    // The browser's own events are the fast path: a cable pulled out is known
    // instantly, and waiting up to thirty seconds to say so would be wrong on
    // the one occasion the cashier is watching.
    window.addEventListener('online', onBrowserOnline)
    window.addEventListener('offline', onBrowserOffline)
  }

  void probe()

  return stopConnectivityWatch
}

export function stopConnectivityWatch(): void {
  if (timer !== null) {
    clearInterval(timer)
    timer = null
    window.removeEventListener('online', onBrowserOnline)
    window.removeEventListener('offline', onBrowserOffline)
  }
}

function onBrowserOnline() {
  // The interface came back. That is not the same as the server being
  // reachable, so it triggers a probe rather than setting the state.
  void probe()
}

function onBrowserOffline() {
  // This direction *is* trustworthy: no interface means no server, and there is
  // nothing to confirm by asking.
  set('offline')
}

/**
 * Asks the API whether it is there. Single-flight.
 *
 * Concurrent callers share one request, for the same reason `refreshSession`
 * does: a burst of failures would otherwise become a burst of probes at exactly
 * the moment the connection is least able to carry them.
 */
export function probe(): Promise<Connectivity> {
  probing ??= runProbe().finally(() => {
    probing = null
  })

  return probing
}

async function runProbe(): Promise<Connectivity> {
  // The one case the browser is authoritative about.
  if (typeof navigator !== 'undefined' && navigator.onLine === false) {
    return set('offline')
  }

  const abort = new AbortController()
  const timeout = setTimeout(() => {
    abort.abort()
  }, PROBE_TIMEOUT_MS)

  try {
    /*
     * Absolute, and against the API rather than the page.
     *
     * A relative path probes the static host the SPA was served from, which is
     * up by definition — the indicator would read green while the API was
     * unreachable, which is the exact opposite of what it is for. `AppLayout`
     * learned this in Phase 0.5 and the comment there says so.
     *
     * `cache: 'no-store'` because a cached 200 from ten minutes ago is not
     * evidence of anything.
     */
    const response = await fetch(`${API_BASE_URL}/health/ready`, {
      signal: abort.signal,
      cache: 'no-store',
    })

    return set(response.ok ? 'online' : 'offline')
  } catch {
    // A network error, a timeout, or an aborted probe. All of them mean the
    // till cannot rely on the server, which is the only distinction that
    // changes what the register does.
    return set('offline')
  } finally {
    clearTimeout(timeout)
  }
}

function set(state: Connectivity): Connectivity {
  if (state === current) {
    return state
  }

  current = state

  for (const listener of listeners) {
    listener(state)
  }

  return state
}

/** Test seam. Not used by application code. */
export function __setConnectivity(state: Connectivity): void {
  set(state)
}

/** Test seam: forget listeners and stop the timer. */
export function __resetConnectivity(): void {
  stopConnectivityWatch()
  listeners.clear()
  current = 'unknown'
  probing = null
}
