import { describe, expect, it } from 'vitest'
import { EMPTY_CART, cartReducer, type Cart } from '@/features/register/cart'
import type { SaleInFlight } from '@/features/register/storage'
import { isTillIdle } from './updateGate'

/**
 * The rule that keeps a service-worker update out of a sale.
 *
 * Exercised through the real cart reducer rather than hand-built objects, so a
 * change to what `beginSale` or `clear` do shows up here — the gate reads
 * `saleKey`, and a reducer that stopped setting it would make this pass while
 * the till updated mid-payment.
 */

const inFlight: SaleInFlight = {
  saleKey: '2a1f0f1e-0000-7000-8000-000000000000',
  tenders: [{ key: 't1', amountMinor: 500 }],
  registerId: 'r1',
  shiftId: 's1',
  at: Date.now(),
}

function withOneItem(): Cart {
  return cartReducer(EMPTY_CART, {
    type: 'add',
    product: {
      productId: 'p1',
      name: 'Water',
      sku: 'W-1',
      unit: 'Each',
      unitPrice: 1.2,
    },
  })
}

describe('isTillIdle', () => {
  it('is idle with an empty cart and nothing in flight', () => {
    expect(isTillIdle(EMPTY_CART, null)).toBe(true)
  })

  it('is not idle with items in the cart', () => {
    // Twenty scanned items are twenty a cashier would have to ring again.
    expect(isTillIdle(withOneItem(), null)).toBe(false)
  })

  it('is not idle once tendering has begun', () => {
    const tendering = cartReducer(withOneItem(), { type: 'beginSale' })

    expect(isTillIdle(tendering, null)).toBe(false)
  })

  it('is not idle when a sale has an identity but no lines', () => {
    /*
     * The case a cart-emptiness check alone would miss.
     *
     * `saleKey` is minted when tendering starts and survives backing out to
     * edit the basket, so a cashier who removed the last line mid-payment has
     * an empty cart and a live sale. Reloading there loses the GUID that makes
     * the retry safe.
     */
    const started = cartReducer(EMPTY_CART, { type: 'beginSale' })

    expect(started.saleKey).not.toBeNull()
    expect(isTillIdle(started, null)).toBe(false)
  })

  it('is not idle while a payment is unresolved', () => {
    /*
     * The most important one. The cart may be empty and the key cleared, and a
     * submitted sale whose answer was never seen is still outstanding —
     * reloading discards the record `useSaleRecovery` needs, in exactly the
     * case where a customer has already handed over money.
     */
    expect(isTillIdle(EMPTY_CART, inFlight)).toBe(false)
  })

  it('is idle again after the sale is cleared', () => {
    const finished = cartReducer(cartReducer(withOneItem(), { type: 'beginSale' }), {
      type: 'clear',
    })

    expect(isTillIdle(finished, null)).toBe(true)
  })
})
