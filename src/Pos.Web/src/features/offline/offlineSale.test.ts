/**
 * The sale a till builds when there is no server to ask.
 *
 * The amounts themselves are covered by the conformance corpus, which pins this
 * engine to the C# one over 913 baskets. What is checked here is everything
 * *around* the arithmetic — the parts that only exist offline, and that the
 * corpus therefore says nothing about.
 */

import { describe, expect, it } from 'vitest'
import { EMPTY_CART, cartReducer, type Cart } from '@/features/register/cart'
import type { MirroredSettings } from '@/lib/offline/db'
import { InvalidDiscountError } from '@/lib/pricing'
import { buildOfflineSale, type OfflineLineInput } from './offlineSale'

const settings: MirroredSettings = {
  currencyCode: 'EUR',
  timeZoneId: 'Europe/Dublin',
  taxMode: 'Inclusive',
  serviceMode: 'Retail',
  cashRoundingIncrement: '0',
  businessDayStartOffset: '04:00:00',
  addressLine: null,
  taxNumber: null,
  receiptHeader: null,
  receiptFooter: null,
}

/** Water at €1.20 inclusive of 23% — the sale Phase 8 rang against production. */
const waterLine: OfflineLineInput = {
  productId: 'p1',
  description: 'Still water 500ml',
  quantity: '1',
  unitPrice: '1.2000',
  taxRate: '0.2300',
  lineDiscount: '0',
}

function tenderedCart(): Cart {
  const withItem = cartReducer(EMPTY_CART, {
    type: 'add',
    product: {
      productId: 'p1',
      name: 'Still water 500ml',
      sku: 'W-1',
      unit: 'Each',
      unitPrice: 1.2,
    },
  })

  return cartReducer(withItem, { type: 'beginSale' })
}

function build(overrides: Partial<Parameters<typeof buildOfflineSale>[0]> = {}) {
  return buildOfflineSale({
    cart: tenderedCart(),
    registerId: 'r1',
    shiftId: 's1',
    tenders: [{ key: 't1', amountMinor: 200 }],
    settings,
    lines: [waterLine],
    now: new Date('2026-08-11T17:40:00.000Z'),
    ...overrides,
  })
}

describe('the request it builds', () => {
  it('carries the cart key as both the transaction id and the idempotency key', () => {
    // The same GUID in both places, exactly as the online path sends it — the
    // server's domain guarantee and its transport guarantee are keyed on it.
    const cart = tenderedCart()
    const sale = build({ cart })

    expect((sale.body as { clientTransactionId: string }).clientTransactionId).toBe(cart.saleKey)
  })

  it('refuses a cart with no sale key rather than minting one', () => {
    // Minting here would make the idempotency header decorative: a retry would
    // carry a GUID the server had never seen, so it would be new work and the
    // customer would pay twice.
    expect(() => build({ cart: EMPTY_CART })).toThrow(/beginSale/)
  })

  it('stamps occurredAt from the moment the sale completed', () => {
    expect(build().occurredAt).toBe('2026-08-11T17:40:00.000Z')
    expect((build().body as { occurredAt: string }).occurredAt).toBe('2026-08-11T17:40:00.000Z')
  })

  it('gives two builds of the same cart different timestamps, which is why it is stored', () => {
    /*
     * The trap, stated as a test.
     *
     * `occurredAt` is in the body and therefore in the fingerprint the server
     * hashes. If the outbox rebuilt the request on each attempt instead of
     * replaying the stored one, every retry would be a different body under the
     * same key — `409 idempotency-key-reused`, for ever, which at a till reads
     * as a sale that will not go through.
     */
    const first = build({ now: new Date('2026-08-11T17:40:00.000Z') })
    const second = build({ now: new Date('2026-08-11T17:40:01.000Z') })

    expect(first.occurredAt).not.toBe(second.occurredAt)
  })

  it('sends tenders as decimals, converted from integer minor units at the edge', () => {
    const body = build({ tenders: [{ key: 't1', amountMinor: 2000 }] }).body as {
      tenders: { amount: number; method: string }[]
    }

    expect(body.tenders).toEqual([{ method: 'Cash', amount: 20, reference: null }])
  })
})

describe('what the cashier is shown', () => {
  it('prices €1.20 inclusive of 23% the way the server does', () => {
    // The production smoke sale. The corpus proves this holds across 913 carts;
    // this is the one somebody has actually seen on a receipt.
    const { display } = build()

    expect(display.total).toBe('1.20')
    expect(display.taxTotal).toBe('0.22')
    expect(display.subtotal).toBe('0.98')
  })

  it('computes change from the total, not from the tendered amount alone', () => {
    expect(build({ tenders: [{ key: 't1', amountMinor: 200 }] }).display.change).toBe('0.80')
  })

  it('shows no change on exact money', () => {
    expect(build({ tenders: [{ key: 't1', amountMinor: 120 }] }).display.change).toBe('0.00')
  })

  it('never shows negative change', () => {
    // An under-tender is refused before this point, and a panel reading
    // "-€3.80" would suggest the till was willing to pay the customer.
    expect(build({ tenders: [{ key: 't1', amountMinor: 50 }] }).display.change).toBe('0.00')
  })

  it('keeps every amount as a decimal string, never a float', () => {
    // 0.1 + 0.2 !== 0.3 is why. A display value assembled through a JS number
    // would drift from the server's by a penny on some baskets and not others.
    const { display } = build()

    for (const amount of [display.total, display.taxTotal, display.subtotal]) {
      expect(typeof amount).toBe('string')
    }
  })
})

describe('refusals', () => {
  it('refuses a basket the server would refuse too', () => {
    /*
     * A line discount larger than its line. Caught here rather than discovered
     * as a permanent sync failure hours later — with the money already in the
     * drawer and the customer long gone.
     */
    expect(() => build({ lines: [{ ...waterLine, lineDiscount: '5.0000' }] })).toThrow(
      InvalidDiscountError,
    )
  })
})

describe('cash rounding', () => {
  it('applies the smallest coin and records the adjustment rather than absorbing it', () => {
    // €4.97 is paid as €4.95 where 1c and 2c coins have been withdrawn, and the
    // 2c is stored rather than nudged away — otherwise the drawer is short by an
    // amount nothing in the system explains.
    const { display } = build({
      settings: { ...settings, taxMode: 'Exclusive', cashRoundingIncrement: '0.05' },
      lines: [{ ...waterLine, unitPrice: '4.9700', taxRate: '0' }],
      tenders: [{ key: 't1', amountMinor: 500 }],
    })

    expect(display.total).toBe('4.95')
    expect(display.roundingAdjustment).toBe('-0.02')
  })
})
