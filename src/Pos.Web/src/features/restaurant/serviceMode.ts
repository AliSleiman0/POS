/**
 * Which front-of-house model this shop runs, as the app decides which screen to show.
 *
 * **Read from the offline mirror first, the server second.** `GET /catalog/sync`
 * already carries `serviceMode` into the local database on every sync — it was
 * put there in 10.0 for exactly this — and the mirror answers instantly and with
 * the line down. `GET /settings` is `CanSell` so every till may call it, and it
 * is the fallback for a browser with no IndexedDB and the source of truth for a
 * shop that has just switched mode.
 *
 * **The unknown state is not `Retail`.** Defaulting while the answer is in
 * flight would flash the retail till on every load in a restaurant, which looks
 * exactly like the bug where the mode does not stick. Callers render nothing
 * until `mode` is non-null; it resolves from the mirror in a millisecond when
 * there is one.
 */

import { useEffect, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { api, unwrap } from '@/api/client'
import { CATALOG_STALE_MS } from '@/app/queryClient'
import { useOffline } from '@/features/offline/offlineContext'
import { readSettings } from '@/lib/offline/catalog'

export type ServiceMode = 'Retail' | 'Restaurant'

export const serviceModeKeys = {
  settings: () => ['settings'] as const,
}

/**
 * The shop's service mode, or `null` while it is still unknown.
 *
 * `isRestaurant` is deliberately **not** `mode !== 'Retail'`: while the answer
 * is unknown that expression is true, and a retail till would render a floor
 * plan for a frame. It is `mode === 'Restaurant'`, so unknown is neither.
 */
export function useServiceMode(): {
  mode: ServiceMode | null
  isRestaurant: boolean
  isRetail: boolean
} {
  const offline = useOffline()
  const [mirrored, setMirrored] = useState<ServiceMode | null>(null)

  const { db } = offline

  useEffect(() => {
    if (db === null) {
      setMirrored(null)
      return
    }

    let cancelled = false

    void readSettings(db).then((settings) => {
      if (!cancelled && settings !== null) {
        setMirrored(settings.serviceMode)
      }
    })

    return () => {
      cancelled = true
    }
  }, [db])

  // CanSell, so this is not a privileged read — see docs/API.md. Catalog
  // staleness rather than LIVE_QUERY_OPTIONS: a shop changes how it serves
  // roughly never, and this read happens on every mount of the till.
  const settings = useQuery({
    queryKey: serviceModeKeys.settings(),
    queryFn: () => unwrap(api.GET('/api/v1/settings')),
    staleTime: CATALOG_STALE_MS,
  })

  // The server wins once it answers: a shop that switched mode two minutes ago
  // has a mirror that still says the old one, and the whole point of the switch
  // being reversible is that it takes effect without waiting for a sync.
  const fromServer = settings.data?.serviceMode ?? null
  const mode: ServiceMode | null = fromServer ?? mirrored

  return {
    mode,
    isRestaurant: mode === 'Restaurant',
    isRetail: mode === 'Retail',
  }
}
