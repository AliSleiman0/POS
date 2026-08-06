import { afterEach, describe, expect, it, vi } from 'vitest'
import { EMPTY_CART, cartReducer, type Cart, type CartProduct } from './cart'
import {
  clearCart,
  clearSaleInFlight,
  readCart,
  readSaleInFlight,
  writeCart,
  writeSaleInFlight,
} from './storage'

/**
 * What the register keeps across a reload.
 *
 * Two claims are being pinned. The obvious one is that a cart round-trips. The
 * one that matters more is that **nothing here throws** — a till whose stored
 * payload is malformed must still open, because reloading is the only remedy a
 * cashier on a shop floor has, and a crash on load makes it useless.
 */
describe('register storage', () => {
  const water: CartProduct = {
    productId: 'p-water',
    name: 'Still Water 500ml',
    sku: 'SKU-1001',
    unit: 'Each',
    unitPrice: 1.2,
  }

  const CART_KEY = 'pos.register.cart'

  function tenderedCart(): Cart {
    return cartReducer(cartReducer(EMPTY_CART, { type: 'add', product: water }), {
      type: 'beginSale',
    })
  }

  afterEach(() => {
    sessionStorage.clear()
    vi.restoreAllMocks()
  })

  it('round-trips a cart, sale key included', () => {
    const cart = tenderedCart()

    writeCart(cart)

    const restored = readCart()

    expect(restored?.lines).toHaveLength(1)
    expect(restored?.lines[0]?.productId).toBe('p-water')

    // The whole reason the key lives in the cart rather than beside it. Without
    // this, a reload mid-payment comes back with a fresh GUID and the retry
    // charges the customer a second time.
    expect(restored?.saleKey).toBe(cart.saleKey)
  })

  it('does not restore a flash, because the scan that caused it is over', () => {
    const cart = cartReducer(EMPTY_CART, { type: 'add', product: water })

    expect(cart.flashedKey).not.toBeNull()

    writeCart(cart)

    // A 400ms highlight replayed on load would animate a line for an event that
    // happened before the page went away.
    expect(readCart()?.flashedKey).toBeNull()
  })

  it('reads nothing back when nothing was written', () => {
    expect(readCart()).toBeNull()
    expect(readSaleInFlight()).toBeNull()
  })

  it('drops a payload it cannot parse rather than throwing', () => {
    sessionStorage.setItem(CART_KEY, 'not json {{{')

    expect(readCart()).toBeNull()

    // And removes it, so the next load is not the same failure again.
    expect(sessionStorage.getItem(CART_KEY)).toBeNull()
  })

  it('drops a payload written by an older version', () => {
    // The reason there is a version at all: a shape change between deploys must
    // cost a re-scan, not a till that cannot be loaded. Migrating badly is worse
    // than dropping.
    sessionStorage.setItem(CART_KEY, JSON.stringify({ version: 0, data: { lines: [] } }))

    expect(readCart()).toBeNull()
  })

  it('drops a payload whose shape is wrong, however plausible the wrapper', () => {
    sessionStorage.setItem(
      CART_KEY,
      JSON.stringify({ version: 1, data: { lines: [{ productId: 'p-water' }] } }),
    )

    // A line missing quantity and price would reach `toSaleLines` and be sold as
    // `undefined` of something. Storage is untrusted input; it is a string a
    // person can edit.
    expect(readCart()).toBeNull()
  })

  it('keeps selling when storage refuses to be written to', () => {
    // Safari in private mode, and any webview with storage disabled. The till
    // loses the basket on a reload — which is where this whole feature started —
    // but it does not fall over mid-sale.
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new DOMException('QuotaExceededError')
    })

    expect(() => {
      writeCart(tenderedCart())
    }).not.toThrow()
  })

  it('keeps selling when storage refuses to be read', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new DOMException('SecurityError')
    })

    expect(readCart()).toBeNull()
    expect(readSaleInFlight()).toBeNull()
  })

  it('round-trips an in-flight sale with the till and the amounts that were taken', () => {
    const record = {
      saleKey: '11111111-2222-3333-4444-555555555555',
      tenders: [{ key: 't1', amountMinor: 1000 }],
      registerId: 'r-1',
      shiftId: 's-1',
      at: 1_760_000_000_000,
    }

    writeSaleInFlight(record)

    const restored = readSaleInFlight()

    expect(restored).toEqual(record)

    // registerId is what stops a tab restored at a different till from adopting
    // this sale, so it has to survive the round trip specifically.
    expect(restored?.registerId).toBe('r-1')
  })

  it('clears each key without disturbing the other', () => {
    writeCart(tenderedCart())
    writeSaleInFlight({
      saleKey: 'k',
      tenders: [],
      registerId: 'r-1',
      shiftId: 's-1',
      at: 0,
    })

    clearSaleInFlight()

    // The sale is resolved; the basket is still on the screen in front of the
    // cashier and must not vanish with it.
    expect(readSaleInFlight()).toBeNull()
    expect(readCart()).not.toBeNull()

    clearCart()

    expect(readCart()).toBeNull()
  })
})
