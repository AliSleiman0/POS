/**
 * TanStack Query configuration.
 *
 * Server state lives here; local UI state lives in React. No global store for
 * data the server owns (ARCHITECTURE.md) — a cache with invalidation is the
 * right tool, and hand-rolled sync between a store and the server is where
 * staleness bugs live.
 *
 * **Staleness is not one number.** A product's name being a minute out of date
 * costs nothing. A shift's state being a minute out of date means a till thinks
 * a drawer is open that a manager closed, and the sale it allows is refused by
 * the server anyway — after the customer has handed over cash. So the two are
 * configured separately and deliberately.
 */

import { QueryClient } from '@tanstack/react-query'
import { isProblemError } from '@/api/problem'

/** Catalog reads: a product edited elsewhere can be a minute stale. */
export const CATALOG_STALE_MS = 60_000

/**
 * Money- and drawer-adjacent reads. Always refetched on mount.
 *
 * Spread into the query options of `GET /shifts/current`, stock levels, and
 * anything else whose staleness has a cash consequence.
 */
export const LIVE_QUERY_OPTIONS = {
  staleTime: 0,
  refetchOnMount: 'always',
} as const

export function createQueryClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      queries: {
        // A POS sits open on one screen for a whole shift. Refetching on every
        // window focus hammers the API from an idle till for no benefit.
        refetchOnWindowFocus: false,
        staleTime: CATALOG_STALE_MS,

        retry: (failureCount, error) => {
          // A 4xx is an answer, not a blip: retrying a 403 or a 404 three times
          // just delays the message by a second and a half. Retry only what
          // could plausibly differ next time.
          if (isProblemError(error) && error.status < 500) {
            return false
          }

          return failureCount < 2
        },
      },

      mutations: {
        // Never automatic. Money- and stock-moving writes are made safe to
        // retry by the idempotency key (CLAUDE.md invariant 6), and a retry
        // that reuses the key is a deliberate act by the caller — not something
        // the cache does on its own with a key it may or may not have kept.
        retry: false,
      },
    },
  })
}
