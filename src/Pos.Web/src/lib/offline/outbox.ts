/**
 * Sales the till has taken and the server has not acknowledged.
 *
 * **A sale is written here before any network attempt is made.** That ordering
 * is the feature, and it generalises what `RegisterPage` already does with the
 * in-flight record: if the page dies between the write and the response — a
 * reload, a crashed tab, a tablet that slept — the record is what lets the till
 * come back and finish the job instead of guessing. Written after the response
 * it would exist only in the cases that do not need it.
 *
 * **Durable, unlike the cart.** CLAUDE.md invariant 11 sends the cart and the
 * refresh token to `sessionStorage` so a shared till hands the next shift
 * nothing, and both of those are right. A queued sale is a third thing: money
 * that has already changed hands. Losing a basket on a tab close is the correct
 * direction to fail; losing a sale a customer paid for is not, and no amount of
 * shared-device hygiene makes it so. Nothing in here is a credential.
 *
 * The record carries the **whole request body**, ready to send unchanged. Not a
 * cart to be rebuilt: rebuilding is a second code path that can disagree with
 * the first, and it would re-read the clock into `occurredAt` — which is part of
 * the idempotency fingerprint, so every retry would be a new body under a spent
 * key and the server would refuse it for ever.
 */

import type { OfflineDb, OutboxDisplay, OutboxSale } from './db'

/** How long to wait before the nth retry. */
const BACKOFF_MS = [0, 2_000, 10_000, 30_000, 120_000, 600_000]

export interface EnqueueInput {
  saleKey: string
  tenantKey: string
  registerId: string
  shiftId: string
  occurredAt: string
  body: unknown
  display: OutboxDisplay
}

/**
 * Records a sale as owed to the server.
 *
 * Idempotent on `saleKey`: enqueuing the same sale twice — a double-tap, a
 * retry from a component that re-rendered — leaves one record and does not
 * reset its attempt count. The key is the store's key, so this is the database
 * enforcing it rather than a check that could race.
 */
export async function enqueue(db: OfflineDb, input: EnqueueInput): Promise<OutboxSale> {
  const existing = await db.get('outbox', input.saleKey)

  if (existing !== undefined) {
    return existing
  }

  const record: OutboxSale = {
    ...input,
    status: 'pending',
    attempts: 0,
    nextAttemptAt: 0,
    lastError: null,
    lastErrorType: null,
    createdAt: Date.now(),
  }

  await db.put('outbox', record)

  return record
}

/** Drops a sale the server has confirmed. */
export async function acknowledge(db: OfflineDb, saleKey: string): Promise<void> {
  await db.delete('outbox', saleKey)
}

/**
 * The queue, oldest first.
 *
 * Order matters: sales replay in the order they were taken, so the sale numbers
 * the server assigns run in the same order the customers did.
 */
export async function listPending(db: OfflineDb): Promise<OutboxSale[]> {
  const all = await db.getAllFromIndex('outbox', 'status', 'pending')

  return all.sort((left, right) => left.createdAt - right.createdAt)
}

/** Sales the server refused permanently. These need a person, not a retry. */
export async function listForReview(db: OfflineDb): Promise<OutboxSale[]> {
  const all = await db.getAllFromIndex('outbox', 'status', 'failed')

  return all.sort((left, right) => left.createdAt - right.createdAt)
}

export async function countPending(db: OfflineDb): Promise<number> {
  return db.countFromIndex('outbox', 'status', 'pending')
}

export async function countForReview(db: OfflineDb): Promise<number> {
  return db.countFromIndex('outbox', 'status', 'failed')
}

export async function get(db: OfflineDb, saleKey: string): Promise<OutboxSale | undefined> {
  return db.get('outbox', saleKey)
}

/**
 * Records a failed attempt that is worth making again.
 *
 * Exponential backoff, capped. The cap matters more than the growth: a till
 * that has been offline all morning should be trying every ten minutes, not
 * once a day, because the cashier is watching for the queue to drain.
 */
export async function deferRetry(
  db: OfflineDb,
  saleKey: string,
  error: string,
  now: number = Date.now(),
): Promise<void> {
  const record = await db.get('outbox', saleKey)

  if (record === undefined) {
    return
  }

  const attempts = record.attempts + 1
  const wait = BACKOFF_MS[Math.min(attempts, BACKOFF_MS.length - 1)]!

  await db.put('outbox', {
    ...record,
    attempts,
    nextAttemptAt: now + wait,
    lastError: error,
    lastErrorType: null,
  })
}

/**
 * Moves a sale to the review queue.
 *
 * **For a refusal that will not change**, and telling the two apart is the
 * whole job of `replay.ts`'s classifier. Retrying a permanent failure is not
 * merely wasted: it means the sale never reaches a person, so a real one sits
 * in a queue for ever while the till reports that it is "pending" — which reads
 * to a cashier as "it will sort itself out".
 */
export async function sendToReview(
  db: OfflineDb,
  saleKey: string,
  error: string,
  errorType: string | null,
): Promise<void> {
  const record = await db.get('outbox', saleKey)

  if (record === undefined) {
    return
  }

  await db.put('outbox', {
    ...record,
    status: 'failed',
    attempts: record.attempts + 1,
    lastError: error,
    lastErrorType: errorType,
  })
}

/**
 * Puts a reviewed sale back in the queue under a **new** identity.
 *
 * Used by the reconciliation screen when a person has decided a refused sale
 * should be filed against the currently open shift. The body changes — a
 * different `shiftId` — so it is genuinely new work and needs a key the server
 * has not seen: reusing the old one is answered `409 idempotency-key-reused`,
 * correctly, because that key already bought something else.
 *
 * **`occurredAt` is carried over unchanged.** The trade happened when it
 * happened; re-dating it to now would move a sale into a trading day it did not
 * belong to, which is the failure this phase's whole timestamp story exists to
 * prevent.
 */
export async function refile(
  db: OfflineDb,
  saleKey: string,
  next: { saleKey: string; shiftId: string; body: unknown },
): Promise<void> {
  const record = await db.get('outbox', saleKey)

  if (record === undefined) {
    return
  }

  const tx = db.transaction('outbox', 'readwrite')

  await Promise.all([
    tx.store.delete(saleKey),
    tx.store.put({
      ...record,
      saleKey: next.saleKey,
      shiftId: next.shiftId,
      body: next.body,
      status: 'pending',
      attempts: 0,
      nextAttemptAt: 0,
      lastError: null,
      lastErrorType: null,
    }),
  ])

  await tx.done
}

/** Forgets a reviewed sale outright. The screen asks twice before calling this. */
export async function discard(db: OfflineDb, saleKey: string): Promise<void> {
  await db.delete('outbox', saleKey)
}

/** The oldest queued sale's age in ms, or null when the queue is empty. */
export async function oldestPendingAge(
  db: OfflineDb,
  now: number = Date.now(),
): Promise<number | null> {
  const pending = await listPending(db)

  return pending.length === 0 ? null : now - pending[0]!.createdAt
}
