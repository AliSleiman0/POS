/**
 * Owns the till's local database, its connectivity signal, and when each of
 * them is refreshed.
 *
 * Mounted inside `AppLayout`, so it exists exactly while somebody is signed in
 * — the database is per tenant and there is no tenant before a login. It sits
 * **outside** `CartProvider` in the tree so the cart can read the offline state
 * without a cycle.
 *
 * **Nothing here throws into the render tree.** Every failure — IndexedDB
 * disabled, a sync that could not reach the server, a replay that found the
 * connection gone again — leaves the app working online and shows itself in the
 * status chip. A till that would not open because its cache was unavailable
 * would be a worse product than one with no cache at all.
 */

import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { useAuth } from '@/auth/authContext'
import { getEnrolledRegisterId } from '@/auth/deviceToken'
import { mirrorSize as readMirrorSize, readMeta } from '@/lib/offline/catalog'
import {
  getConnectivity,
  onConnectivityChange,
  probe,
  startConnectivityWatch,
  type Connectivity,
} from '@/lib/offline/connectivity'
import { openOfflineDb, type OfflineDb } from '@/lib/offline/db'
import { countForReview, countPending } from '@/lib/offline/outbox'
import { replayOutbox } from '@/lib/offline/replay'
import { syncCatalog } from '@/lib/offline/sync'
import { OfflineContext, type OfflineState } from './offlineContext'

/**
 * How often to re-sync the catalog while online and idle.
 *
 * Five minutes. A price change made in the back office should reach the till
 * within a few minutes without anybody doing anything; more often than this is
 * traffic for a catalog that changes a handful of times a day.
 */
const SYNC_INTERVAL_MS = 5 * 60_000

export function OfflineProvider({ children }: { children: ReactNode }) {
  const { status, tenant, registerId } = useOfflineSession()

  const [db, setDb] = useState<OfflineDb | null>(null)
  const [connectivity, setConnectivity] = useState<Connectivity>(getConnectivity)
  const [mirroredAt, setMirroredAt] = useState<number | null>(null)
  const [mirrorSize, setMirrorSize] = useState(0)
  const [pendingSales, setPendingSales] = useState(0)
  const [reviewSales, setReviewSales] = useState(0)
  const [syncing, setSyncing] = useState(false)

  /** Guards against two syncs overlapping — a reconnect landing on a tick. */
  const running = useRef(false)

  // Open the tenant's database. Re-opened when the tenant changes, which is a
  // real case on a shared tablet: one shop signs out and another signs in, and
  // the second must never read the first's mirror or replay its queue.
  useEffect(() => {
    if (tenant === null) {
      setDb(null)
      return
    }

    let cancelled = false

    openOfflineDb(tenant).then(
      (opened) => {
        if (!cancelled) {
          setDb(opened)
        }
      },
      () => {
        // IndexedDB unavailable. Online-only from here, which the status chip
        // says out loud rather than pretending the mirror is simply empty.
        if (!cancelled) {
          setDb(null)
        }
      },
    )

    return () => {
      cancelled = true
    }
  }, [tenant])

  useEffect(() => startConnectivityWatch(), [])

  useEffect(() => onConnectivityChange(setConnectivity), [])

  const refresh = useCallback(async () => {
    if (db === null) {
      return
    }

    const [meta, size, pending, review] = await Promise.all([
      readMeta(db),
      readMirrorSize(db),
      countPending(db),
      countForReview(db),
    ])

    setMirroredAt(meta.syncedAt)
    setMirrorSize(size)
    setPendingSales(pending)
    setReviewSales(review)
  }, [db])

  /**
   * One pass: send what is owed, then pull what changed.
   *
   * **The outbox goes first, deliberately.** A queued sale is money that has
   * already changed hands and the catalog is a convenience; if the connection
   * only holds for one of the two, that is the one worth spending it on.
   */
  const sync = useCallback(async () => {
    if (db === null || running.current || tenant === null) {
      return
    }

    running.current = true
    setSyncing(true)

    try {
      await replayOutbox(db, { tenantKey: tenant, registerId })
      await syncCatalog(db)
    } catch {
      // Offline, or the server refused. The mirror keeps whatever it had and
      // the queue keeps whatever it had; the age on screen is what tells the
      // cashier how much to trust the first.
    } finally {
      running.current = false
      setSyncing(false)
      await refresh()
    }
  }, [db, refresh, registerId, tenant])

  // On sign-in and whenever the database opens.
  useEffect(() => {
    if (db !== null && status === 'authenticated') {
      void sync()
    }
  }, [db, status, sync])

  // On reconnect. The probe is what decides "online", not `navigator.onLine`.
  useEffect(
    () =>
      onConnectivityChange((state) => {
        if (state === 'online') {
          void sync()
        }
      }),
    [sync],
  )

  // And on a timer while the app is open.
  useEffect(() => {
    const timer = setInterval(() => {
      void probe().then((state) => (state === 'online' ? sync() : undefined))
    }, SYNC_INTERVAL_MS)

    return () => {
      clearInterval(timer)
    }
  }, [sync])

  const value = useMemo<OfflineState>(
    () => ({
      db,
      connectivity,
      mirroredAt,
      mirrorSize,
      pendingSales,
      reviewSales,
      syncing,
      sync,
      refresh,
    }),
    [db, connectivity, mirroredAt, mirrorSize, pendingSales, reviewSales, syncing, sync, refresh],
  )

  return <OfflineContext value={value}>{children}</OfflineContext>
}

/**
 * The bits of the session the offline layer needs, in one place.
 *
 * `registerId` comes from enrolment rather than from the token: reading it off
 * the access token would work only after a *PIN* login, so a manager signed in
 * by password at the same till would own none of its queued sales — and
 * `ownsRecord` would then refuse to replay a queue the till itself created.
 */
function useOfflineSession() {
  const { status, tenant } = useAuth()

  return {
    status,
    // The slug: /auth/me carries no tenant id, and the slug is unique per
    // tenant with no route that changes it.
    tenant: tenant?.slug ?? null,
    registerId: getEnrolledRegisterId(),
  }
}
