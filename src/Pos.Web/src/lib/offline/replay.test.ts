/**
 * How a failed replay is classified, and whose queue a till will send.
 *
 * These two decisions are where the outbox does damage when it is wrong, and
 * they fail in opposite directions:
 *
 * - Classify a **permanent** refusal as retryable and a real sale sits in the
 *   queue for ever while the till reports "pending" — which a cashier reads as
 *   "it will sort itself out".
 * - Classify a **transient** one as permanent and a person is asked to
 *   adjudicate a timeout.
 * - Replay a record this till does not own and another shop's or another
 *   drawer's takings go through this register.
 */

import { describe, expect, it } from 'vitest'
import { ProblemError } from '@/api/problem'
import type { OutboxSale } from './db'
import { classify, ownsRecord } from './replay'

function problem(status: number, type?: string): ProblemError {
  return new ProblemError(status, {
    status,
    title: 'Failed',
    detail: 'Something went wrong.',
    ...(type === undefined ? {} : { type: `https://pos.example/errors/${type}` }),
  })
}

const record: OutboxSale = {
  saleKey: 'k1',
  tenantKey: 'harbour-stores',
  registerId: 'r1',
  shiftId: 's1',
  occurredAt: '2026-08-11T17:40:00.000Z',
  body: {},
  display: {
    lines: [],
    subtotal: '0.00',
    taxTotal: '0.00',
    discountTotal: '0.00',
    roundingAdjustment: '0.00',
    total: '0.00',
    tendered: '0.00',
    change: '0.00',
    currencyCode: 'EUR',
  },
  status: 'pending',
  attempts: 0,
  nextAttemptAt: 0,
  lastError: null,
  lastErrorType: null,
  createdAt: 0,
}

describe('classify', () => {
  it('retries a transport failure', () => {
    // The commonest outcome by far, and the whole reason the queue exists.
    expect(classify(new TypeError('Failed to fetch')).kind).toBe('retry')
  })

  it('retries a server error', () => {
    expect(classify(problem(500)).kind).toBe('retry')
    expect(classify(problem(503)).kind).toBe('retry')
  })

  it('retries a timeout and a rate limit, which are 4xx but transient', () => {
    /*
     * The two deliberate exceptions to "a 4xx is an answer". A till whose queue
     * drains onto a recovering connection is the most likely thing in the
     * system to meet a 429, and sending a morning's takings to a manager for it
     * would be absurd.
     */
    expect(classify(problem(408)).kind).toBe('retry')
    expect(classify(problem(429)).kind).toBe('retry')
  })

  it('reviews a validation failure', () => {
    // The server has decided. Retrying sends the same body and gets the same
    // answer, for ever.
    expect(classify(problem(400)).kind).toBe('review')
  })

  it('reviews a closed shift, which is the case 9.4 exists for', () => {
    const outcome = classify(problem(409, 'shift-closed'))

    expect(outcome.kind).toBe('review')
    expect(outcome.kind === 'review' && outcome.type).toBe('shift-closed')
  })

  it('reviews a reused idempotency key and carries the type through', () => {
    /*
     * Permanent *and* significant. It means an earlier attempt with this key
     * landed carrying a different body — so the sale may already exist, and
     * re-sending anything is the one thing that must not happen. The review
     * screen needs the type to be able to say so.
     */
    const outcome = classify(problem(409, 'idempotency-key-reused'))

    expect(outcome.kind).toBe('review')
    expect(outcome.kind === 'review' && outcome.type).toBe('idempotency-key-reused')
  })

  it('reviews a refused offline timestamp rather than backing off against it', () => {
    // The 422 the server answers a till with a wrong clock. Retrying changes
    // nothing; a person has to confirm what was taken.
    const outcome = classify(problem(422, 'offline-sale-timestamp-invalid'))

    expect(outcome.kind).toBe('review')
    expect(outcome.kind === 'review' && outcome.type).toBe('offline-sale-timestamp-invalid')
  })

  it('reviews an authorization failure', () => {
    expect(classify(problem(403, 'override-required')).kind).toBe('review')
  })
})

describe('ownsRecord', () => {
  it('accepts the till and tenant that took the sale', () => {
    expect(ownsRecord(record, { tenantKey: 'harbour-stores', registerId: 'r1' })).toBe(true)
  })

  it('refuses another tenant', () => {
    // A shared counter tablet signed into a second shop. Replaying here would
    // put one shop's takings through another's register.
    expect(ownsRecord(record, { tenantKey: 'other-shop', registerId: 'r1' })).toBe(false)
  })

  it('refuses another register', () => {
    // A device re-enrolled since, or a tab restored on a different till. The
    // sale belongs to a drawer this one is not.
    expect(ownsRecord(record, { tenantKey: 'harbour-stores', registerId: 'r2' })).toBe(false)
  })

  it('refuses a session with no enrolled register at all', () => {
    // A manager signed in by password on a browser that is not a till. Nothing
    // it holds should be sent from here.
    expect(ownsRecord(record, { tenantKey: 'harbour-stores', registerId: null })).toBe(false)
  })
})
