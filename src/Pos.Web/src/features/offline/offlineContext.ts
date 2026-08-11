import { createContext, use } from 'react'
import type { OfflineDb } from '@/lib/offline/db'
import type { Connectivity } from '@/lib/offline/connectivity'
import type { PersistenceState } from '@/lib/offline/persist'

/** What the offline layer tells the rest of the app. */
export interface OfflineState {
  /**
   * The tenant's local database, or `null` where there is not one.
   *
   * `null` is an ordinary state, not an error: signed out, or a browser with
   * IndexedDB disabled — Safari in private mode, a locked-down webview. Every
   * caller has to handle it, and the app stays usable online when it does.
   */
  db: OfflineDb | null

  connectivity: Connectivity

  /** Epoch ms of the last completed sync, or null if the mirror is empty. */
  mirroredAt: number | null

  /** How many products the mirror holds. Shown on the diagnostics panel. */
  mirrorSize: number

  /** Sales taken and not yet acknowledged by the server. */
  pendingSales: number

  /** Sales the server refused permanently. They need a person. */
  reviewSales: number

  /** Whether a catalog sync is running now. */
  syncing: boolean

  /** Whether the browser has promised to keep this till's data. */
  persistence: PersistenceState

  /**
   * A warning worth showing the shop, or null.
   *
   * Null most of the time, and that is the design: a warning shown constantly
   * is one nobody reads, which would cost the real one its meaning.
   */
  risk: string | null

  /** Bring the mirror up to date. Safe to call while one is running. */
  sync: () => Promise<void>

  /** Re-read the counters after the outbox changes. */
  refresh: () => Promise<void>
}

export const OfflineContext = createContext<OfflineState | null>(null)

/**
 * The offline layer.
 *
 * Throws when there is no provider, rather than returning a null-shaped
 * default: a component that reads `pendingSales` from a silently-absent
 * provider would render "0 pending" over a queue that exists, which is the one
 * thing 9.3's status chip must never do.
 */
export function useOffline(): OfflineState {
  const state = use(OfflineContext)

  if (state === null) {
    throw new Error('useOffline was called outside OfflineProvider.')
  }

  return state
}
