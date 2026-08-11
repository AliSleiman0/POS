/**
 * Asking the browser not to throw the till's data away, and finding out whether
 * it agreed.
 *
 * **Browser storage is evictable, and that is the honest headline of Phase 9.**
 * IndexedDB is not a database the shop owns; it is a cache the browser may
 * reclaim under storage pressure, and Safari on iOS may clear it after a period
 * of non-use whatever anyone asks. `navigator.storage.persist()` requests an
 * exemption. It is a request. It can be refused, and on some platforms it is
 * refused silently.
 *
 * So the point of this file is not to make the data safe — nothing on the web
 * can — but to find out **what was actually granted** and to let the app say so.
 * A shop that believes its queued sales are durable, trades all day offline and
 * loses them will not come back, and will tell people. That outcome is worth
 * more than a feature bullet.
 */

export type PersistenceState =
  /** The browser has agreed not to evict this origin's storage. */
  | 'granted'
  /** It has not. Data may be reclaimed at any time. */
  | 'denied'
  /** This browser has no such API, so nothing can be promised either way. */
  | 'unsupported'
  /** Not asked yet. */
  | 'unknown'

export interface StorageReport {
  persistence: PersistenceState
  /** Bytes in use, where the browser will say. */
  usage: number | null
  /** Bytes the browser is willing to hold, where it will say. */
  quota: number | null
}

/**
 * Requests persistent storage, once.
 *
 * Called at enrolment and on sign-in rather than on first sale: a browser may
 * grant it silently based on engagement, so asking early and asking when the
 * user is present are both worth more than asking at the moment it matters.
 *
 * Never throws — a browser that refuses to answer is reported as `unsupported`,
 * which is the truth from the app's point of view.
 */
export async function requestPersistence(): Promise<PersistenceState> {
  if (typeof navigator === 'undefined' || navigator.storage === undefined) {
    return 'unsupported'
  }

  try {
    // Already granted? Asking again is harmless but pointless, and some
    // browsers show a prompt for the second call.
    if (
      typeof navigator.storage.persisted === 'function' &&
      (await navigator.storage.persisted())
    ) {
      return 'granted'
    }

    if (typeof navigator.storage.persist !== 'function') {
      return 'unsupported'
    }

    return (await navigator.storage.persist()) ? 'granted' : 'denied'
  } catch {
    return 'unsupported'
  }
}

/** What the browser currently says about this origin's storage. */
export async function readStorageReport(): Promise<StorageReport> {
  if (typeof navigator === 'undefined' || navigator.storage === undefined) {
    return { persistence: 'unsupported', usage: null, quota: null }
  }

  let persistence: PersistenceState = 'unknown'

  try {
    if (typeof navigator.storage.persisted === 'function') {
      persistence = (await navigator.storage.persisted()) ? 'granted' : 'denied'
    } else {
      persistence = 'unsupported'
    }
  } catch {
    persistence = 'unsupported'
  }

  try {
    if (typeof navigator.storage.estimate !== 'function') {
      return { persistence, usage: null, quota: null }
    }

    const estimate = await navigator.storage.estimate()

    return {
      persistence,
      usage: estimate.usage ?? null,
      quota: estimate.quota ?? null,
    }
  } catch {
    return { persistence, usage: null, quota: null }
  }
}

/**
 * How long a queue may sit before the app starts warning about it.
 *
 * Four hours. Long enough that an ordinary outage does not nag; short enough
 * that a shop trading offline for a whole day is told before it becomes a
 * day's takings sitting in a browser cache.
 */
export const LONG_OFFLINE_MS = 4 * 60 * 60_000

/**
 * How many queued sales is too many to be comfortable about.
 *
 * Twenty. Not a technical limit — IndexedDB will hold far more — but the point
 * at which the amount of money in an evictable cache stops being trivial, and
 * somebody should be deciding whether to keep trading offline.
 */
export const LARGE_QUEUE = 20

/**
 * Whether the shop should be warned, and why.
 *
 * `null` when there is nothing worth saying: a warning shown constantly is one
 * nobody reads, which would cost the two above their meaning.
 */
export function describeRisk(input: {
  pendingSales: number
  oldestPendingAgeMs: number | null
  persistence: PersistenceState
}): string | null {
  if (input.pendingSales === 0) {
    return null
  }

  if (input.persistence === 'denied') {
    return (
      `This browser has not granted persistent storage, and ${String(input.pendingSales)} ` +
      `${input.pendingSales === 1 ? 'sale is' : 'sales are'} saved only on this device. ` +
      'They could be cleared without warning. Get the till back online before taking more.'
    )
  }

  if (input.oldestPendingAgeMs !== null && input.oldestPendingAgeMs > LONG_OFFLINE_MS) {
    const hours = Math.floor(input.oldestPendingAgeMs / 3_600_000)

    return (
      `This till has been unable to reach the server for ${String(hours)} hours and is holding ` +
      `${String(input.pendingSales)} ${input.pendingSales === 1 ? 'sale' : 'sales'}. ` +
      'Offline trading on a browser is best effort — the longer this goes on, the more there is to lose.'
    )
  }

  if (input.pendingSales >= LARGE_QUEUE) {
    return (
      `${String(input.pendingSales)} sales are saved on this till and not yet sent. ` +
      'That is a lot of money in a browser cache. Restoring the connection should be the priority.'
    )
  }

  return null
}
