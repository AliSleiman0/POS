/**
 * Sending the outbox to the server.
 *
 * **This replays ordinary `POST /sales` calls with their original
 * `Idempotency-Key`.** There is no bulk-upload endpoint and no sync API, and
 * that is the point of Phase 3.5 existing before this phase: a second server
 * write path would be a second place for the pricing rules to be wrong, and the
 * two would disagree about a customer's total eventually.
 *
 * Two properties matter more than anything else here.
 *
 * **Serial.** One sale at a time, oldest first. Sales replay in the order they
 * were taken so the numbers the server assigns run in the order the customers
 * did — and, more practically, a queue fired off in parallel on a shop's
 * recovering connection is how the recovery gets lost again.
 *
 * **Classified correctly.** Every failure is either worth retrying or is not,
 * and getting that wrong fails in both directions. Retrying a permanent
 * refusal means a real sale sits in the queue for ever while the till says
 * "pending", which a cashier reads as "it will sort itself out". Reviewing a
 * transient one means a person is asked to adjudicate a timeout.
 */

import { api, unwrapWithResponse } from '@/api/client'
import { wasReplayed } from '@/api/idempotency'
import { isProblemError } from '@/api/problem'
import type { components } from '@/api/schema'
import type { OfflineDb, OutboxSale } from './db'
import { acknowledge, deferRetry, listPending, sendToReview } from './outbox'

type SaleResponse = components['schemas']['SaleResponse']

/** What one attempt did. */
export type AttemptOutcome =
  /** The server has it — freshly written, or replayed from its own record. */
  | { kind: 'accepted'; sale: SaleResponse; replayed: boolean }
  /** Worth trying again: the network, a 5xx, a rate limit. */
  | { kind: 'retry'; reason: string }
  /** Will never succeed as sent. A person has to look at it. */
  | { kind: 'review'; reason: string; type: string | null }

export interface ReplayResult {
  attempted: number
  accepted: number
  retry: number
  review: number
}

/**
 * Whether the caller's session and till own this record.
 *
 * **Refused rather than adapted.** A tab restored under a different login, or a
 * device re-enrolled since, holds a sale that belongs to a drawer this till is
 * not — replaying it would put another shop's or another register's takings
 * through this one. `useSaleRecovery` applies the same rule to the in-flight
 * record for the same reason, and the record is *kept*, not dropped: it is
 * still somebody's money, and the till it belongs to can still send it.
 */
export function ownsRecord(
  record: OutboxSale,
  session: { tenantKey: string; registerId: string | null },
): boolean {
  return record.tenantKey === session.tenantKey && record.registerId === session.registerId
}

/**
 * Sends one sale.
 *
 * Exported so a test can drive a single attempt without a loop, and so the
 * review screen can retry one record on demand.
 */
export async function attempt(record: OutboxSale): Promise<AttemptOutcome> {
  try {
    const { data, response } = await unwrapWithResponse(
      api.POST('/api/v1/sales', {
        params: { header: { 'Idempotency-Key': record.saleKey } },

        // Sent exactly as it was stored. Not rebuilt from a cart: rebuilding is
        // a second code path, and it would re-read the clock into `occurredAt`,
        // which is part of the fingerprint the server hashes.
        body: record.body as never,
      }),
    )

    return { kind: 'accepted', sale: data, replayed: wasReplayed(response) }
  } catch (caught) {
    return classify(caught)
  }
}

/**
 * Which side of the line a failure falls on.
 *
 * The rule: **a 4xx is an answer, a 5xx or a transport failure is a blip** —
 * the same distinction `queryClient`'s retry predicate already makes. Two
 * deliberate exceptions, both of which would otherwise be classified wrongly:
 *
 * - **408 and 429** are 4xx and are entirely transient. A rate-limited till on
 *   a recovering connection is the most likely time to meet one, and sending
 *   the queue to a manager for it would be absurd.
 * - **409 `idempotency-key-reused`** is permanent *and* significant. It means an
 *   earlier attempt with this key landed carrying a different body, so the sale
 *   may already exist — and re-sending anything is the one thing that must not
 *   happen. It goes to review with that stated.
 */
export function classify(caught: unknown): AttemptOutcome {
  if (!isProblemError(caught)) {
    // A transport failure: offline, DNS, a connection cut mid-flight. The most
    // common outcome by far, and the whole reason the queue exists.
    return { kind: 'retry', reason: 'The server could not be reached.' }
  }

  const { status } = caught
  const type = caught.slug ?? null

  if (status >= 500 || status === 408 || status === 429) {
    return { kind: 'retry', reason: caught.message }
  }

  if (status >= 400) {
    return { kind: 'review', reason: caught.message, type }
  }

  // A non-2xx below 400 that reached here is not something to guess about.
  return { kind: 'review', reason: caught.message, type }
}

/**
 * Walks the queue once.
 *
 * **Stops at the first retryable failure.** If one sale cannot reach the server
 * the next will not either, and hammering a dead connection with the whole queue
 * delays the recovery everybody is waiting for. A *reviewable* failure does not
 * stop the walk: that sale is set aside and the rest keep going, because one bad
 * record must not hold up a morning's takings.
 *
 * @param onAccepted invoked per landed sale, so the UI can count down as it goes
 * rather than jumping when the whole queue finishes.
 */
export async function replayOutbox(
  db: OfflineDb,
  session: { tenantKey: string; registerId: string | null },
  onAccepted?: (record: OutboxSale, sale: SaleResponse) => void,
): Promise<ReplayResult> {
  const result: ReplayResult = { attempted: 0, accepted: 0, retry: 0, review: 0 }

  const now = Date.now()

  for (const record of await listPending(db)) {
    if (!ownsRecord(record, session)) {
      continue
    }

    // Backoff. Left where it is rather than skipped over, because the queue is
    // ordered and a later sale must not overtake an earlier one.
    if (record.nextAttemptAt > now) {
      break
    }

    result.attempted += 1

    const outcome = await attempt(record)

    if (outcome.kind === 'accepted') {
      await acknowledge(db, record.saleKey)
      result.accepted += 1
      onAccepted?.(record, outcome.sale)
      continue
    }

    if (outcome.kind === 'review') {
      await sendToReview(db, record.saleKey, outcome.reason, outcome.type)
      result.review += 1
      continue
    }

    await deferRetry(db, record.saleKey, outcome.reason)
    result.retry += 1
    break
  }

  return result
}
