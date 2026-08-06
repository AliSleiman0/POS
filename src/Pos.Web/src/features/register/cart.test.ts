import { describe, expect, it } from 'vitest'
import {
  cartReducer,
  cartSignature,
  EMPTY_CART,
  MAX_LINE_QUANTITY,
  provisionalLineMinor,
  requiredPolicies,
  toSaleLines,
  type Cart,
  type CartProduct,
} from './cart'

/**
 * The cart reducer.
 *
 * Nothing here computes money — that is the server's job and the reason the cart
 * carries products and quantities rather than totals. What it does own is what
 * the cashier chose, and getting *that* wrong is a customer charged for two of
 * something they bought one of.
 */
describe('cartReducer', () => {
  const water: CartProduct = {
    productId: 'p-water',
    name: 'Still Water 500ml',
    sku: 'SKU-1001',
    unit: 'Each',
    unitPrice: 1.2,
  }

  const cheese: CartProduct = {
    productId: 'p-cheese',
    name: 'Irish Cheddar',
    sku: 'SKU-1003',
    unit: 'Kilogram',
    unitPrice: 12.95,
  }

  function cartOf(...products: CartProduct[]): Cart {
    return products.reduce<Cart>(
      (cart, product) => cartReducer(cart, { type: 'add', product }),
      EMPTY_CART,
    )
  }

  it('adds a scanned product as one line of one', () => {
    const cart = cartOf(water)

    expect(cart.lines).toHaveLength(1)
    expect(cart.lines[0]).toMatchObject({
      productId: 'p-water',
      description: 'Still Water 500ml',
      quantity: 1,
      unitPriceMinor: 120,
    })
    // Selected and flashed, so the keyboard acts on what was just scanned and
    // the cashier can see which line moved without looking away from the goods.
    expect(cart.selectedKey).toBe(cart.lines[0]?.key)
    expect(cart.flashedKey).toBe(cart.lines[0]?.key)
  })

  it('makes a second scan of the same item two units of one line', () => {
    const cart = cartOf(water, water)

    // A till that grew a line per scan is unreadable by the third beep.
    expect(cart.lines).toHaveLength(1)
    expect(cart.lines[0]?.quantity).toBe(2)
  })

  it('keeps a decimal quantity exactly', () => {
    let cart = cartOf(cheese)
    const key = cart.lines[0]!.key

    cart = cartReducer(cart, { type: 'setQuantity', key, quantity: 0.35 })

    // 0.350 kg of cheese is an ordinary amount of cheese, and the server stores
    // four decimal places for exactly this.
    expect(cart.lines[0]?.quantity).toBe(0.35)
    expect(toSaleLines(cart)[0]?.quantity).toBe(0.35)
  })

  it('does not let floating point creep into a stepped quantity', () => {
    let cart = cartOf(cheese)
    const key = cart.lines[0]!.key

    cart = cartReducer(cart, { type: 'setQuantity', key, quantity: 0.1 })
    cart = cartReducer(cart, { type: 'adjustQuantity', key, delta: 0.2 })

    // `0.1 + 0.2 === 0.30000000000000004`, which the server would reject as more
    // than four decimal places — after the customer had been quoted a price.
    expect(cart.lines[0]?.quantity).toBe(0.3)
  })

  it('removes a line whose quantity is taken to zero', () => {
    let cart = cartOf(water)
    const key = cart.lines[0]!.key

    cart = cartReducer(cart, { type: 'setQuantity', key, quantity: 0 })

    // An empty line left on screen is a line that gets sold as one.
    expect(cart.lines).toHaveLength(0)
  })

  it('caps a fat-fingered quantity', () => {
    let cart = cartOf(water)
    const key = cart.lines[0]!.key

    cart = cartReducer(cart, { type: 'setQuantity', key, quantity: 1_000_000 })

    expect(cart.lines[0]?.quantity).toBe(MAX_LINE_QUANTITY)
  })

  it("moves the selection to the line that took the removed one's place", () => {
    let cart = cartOf(water, cheese)
    const cheeseKey = cart.lines[1]!.key

    cart = cartReducer(cart, { type: 'select', key: cheeseKey })
    cart = cartReducer(cart, { type: 'remove', key: cheeseKey })

    // Voiding three lines in a row should be three presses of one key.
    expect(cart.selectedKey).toBe(cart.lines[0]?.key)
  })

  it('walks the lines with the arrow keys, and stops at the ends', () => {
    let cart = cartOf(water, cheese)
    cart = cartReducer(cart, { type: 'select', key: null })

    cart = cartReducer(cart, { type: 'move', delta: 1 })
    expect(cart.selectedKey).toBe(cart.lines[0]?.key)

    cart = cartReducer(cart, { type: 'move', delta: 1 })
    expect(cart.selectedKey).toBe(cart.lines[1]?.key)

    // Rather than wrapping: a cursor that jumps from the bottom to the top is
    // how the wrong line gets voided.
    cart = cartReducer(cart, { type: 'move', delta: 1 })
    expect(cart.selectedKey).toBe(cart.lines[1]?.key)
  })

  it('empties completely on a cart void', () => {
    const cart = cartReducer(cartOf(water, cheese), { type: 'clear' })

    expect(cart).toEqual(EMPTY_CART)
  })
})

describe('toSaleLines', () => {
  const product: CartProduct = {
    productId: 'p-water',
    name: 'Still Water 500ml',
    sku: 'SKU-1001',
    unit: 'Each',
    unitPrice: 1.2,
  }

  it('sends no prices — the server has them', () => {
    const cart = cartReducer(EMPTY_CART, { type: 'add', product })

    // A price a client can send is a price a customer can edit (docs/API.md).
    // Discounts and overrides stay null until 5.3 builds the gated controls.
    expect(toSaleLines(cart)).toEqual([
      { productId: 'p-water', quantity: 1, unitPriceOverride: null, discountAmount: null },
    ])
  })
})

describe('cartSignature', () => {
  const product: CartProduct = {
    productId: 'p-water',
    name: 'Still Water 500ml',
    sku: 'SKU-1001',
    unit: 'Each',
    unitPrice: 1.2,
  }

  it('ignores a change that does not change the price', () => {
    const cart = cartReducer(EMPTY_CART, { type: 'add', product })
    const reselected = cartReducer(cart, { type: 'select', key: null })

    // Moving the cursor must not put a spinner over the total.
    expect(cartSignature(reselected)).toBe(cartSignature(cart))
  })

  it('changes when a quantity does', () => {
    const cart = cartReducer(EMPTY_CART, { type: 'add', product })
    const more = cartReducer(cart, { type: 'adjustQuantity', key: cart.lines[0]!.key, delta: 1 })

    expect(cartSignature(more)).not.toBe(cartSignature(cart))
  })
})

describe('provisionalLineMinor', () => {
  it('stays in integers', () => {
    const cart = cartReducer(EMPTY_CART, {
      type: 'add',
      product: {
        productId: 'p-bag',
        name: 'Paper Bag',
        sku: 'SKU-1005',
        unit: 'Each',
        // The catalog's fourth decimal place. Rounded to 17p for the display
        // only — the server prices the real 0.1650 and its answer is the one
        // that reaches the customer.
        unitPrice: 0.165,
      },
      quantity: 3,
    })

    expect(provisionalLineMinor(cart.lines[0]!)).toBe(51)
    expect(Number.isInteger(provisionalLineMinor(cart.lines[0]!))).toBe(true)
  })
})

/**
 * Discounts and price overrides.
 *
 * These are the first fields the cart carries that change what a customer pays,
 * and every one of them is a value a *person typed* rather than one the client
 * worked out — which is why holding them here does not breach invariant 3.
 */
describe('discounts and price overrides', () => {
  const water: CartProduct = {
    productId: 'p-water',
    name: 'Still Water 500ml',
    sku: 'SKU-1001',
    unit: 'Each',
    unitPrice: 1.2,
  }

  function withWater(quantity = 1): Cart {
    return cartReducer(EMPTY_CART, { type: 'add', product: water, quantity })
  }

  it('sends what was typed on the line, through the one function the sale also uses', () => {
    const cart = withWater(2)
    const key = cart.lines[0]!.key

    const discounted = cartReducer(cart, { type: 'setLineDiscount', key, amount: 0.5 })
    const overridden = cartReducer(discounted, { type: 'setPriceOverride', key, unitPrice: 0.99 })

    expect(toSaleLines(overridden)[0]).toEqual({
      productId: 'p-water',
      quantity: 2,
      unitPriceOverride: 0.99,
      discountAmount: 0.5,
    })
  })

  it('treats an empty discount as no discount rather than a discount of nothing', () => {
    // A line reading "−£0.00" looks like a keystroke that failed to register,
    // and the server would price it identically anyway.
    const cart = withWater()
    const key = cart.lines[0]!.key

    expect(
      cartReducer(cart, { type: 'setLineDiscount', key, amount: 0 }).lines[0]!.discountAmount,
    ).toBeNull()
    expect(cartReducer(cart, { type: 'setCartDiscount', amount: 0 }).cartDiscountAmount).toBeNull()
  })

  it('keeps a price override of zero, because "this one is free" is a real decision', () => {
    const cart = withWater()
    const key = cart.lines[0]!.key

    expect(
      cartReducer(cart, { type: 'setPriceOverride', key, unitPrice: 0 }).lines[0]!
        .unitPriceOverride,
    ).toBe(0)
  })

  it('rounds an amount to the four decimals the column stores', () => {
    const cart = withWater()
    const key = cart.lines[0]!.key

    const rounded = cartReducer(cart, { type: 'setLineDiscount', key, amount: 0.123_456 })

    expect(rounded.lines[0]!.discountAmount).toBe(0.1235)
  })

  it('names the policies pricing the cart will need', () => {
    const cart = withWater()
    const key = cart.lines[0]!.key

    expect(requiredPolicies(cart)).toEqual([])
    expect(requiredPolicies(cartReducer(cart, { type: 'setCartDiscount', amount: 1 }))).toEqual([
      'CanApplyDiscount',
    ])
    expect(
      requiredPolicies(cartReducer(cart, { type: 'setLineDiscount', key, amount: 1 })),
    ).toEqual(['CanApplyDiscount'])
    expect(
      requiredPolicies(cartReducer(cart, { type: 'setPriceOverride', key, unitPrice: 1 })),
    ).toEqual(['CanOverridePrice'])
  })

  it('prices the provisional line off the override, less the discount', () => {
    const cart = withWater(2)
    const key = cart.lines[0]!.key

    // 2 × £1.20 = £2.40 provisionally.
    expect(provisionalLineMinor(cart.lines[0]!)).toBe(240)

    const overridden = cartReducer(cart, { type: 'setPriceOverride', key, unitPrice: 1 })
    expect(provisionalLineMinor(overridden.lines[0]!)).toBe(200)

    const discounted = cartReducer(overridden, { type: 'setLineDiscount', key, amount: 0.5 })
    expect(provisionalLineMinor(discounted.lines[0]!)).toBe(150)
  })

  it('never shows a negative line, however large the discount', () => {
    // The server refuses a discount bigger than its line outright. On the way to
    // that refusal the till must not suggest it is willing to pay the customer.
    const cart = withWater()
    const key = cart.lines[0]!.key

    const absurd = cartReducer(cart, { type: 'setLineDiscount', key, amount: 99 })

    expect(provisionalLineMinor(absurd.lines[0]!)).toBe(0)
  })

  it('changes the signature, so the quote is asked again', () => {
    /*
     * The one that matters most, and the one a future change is most likely to
     * break. The quote is cached on this string: an adjustment missing from it
     * leaves the previous total on screen looking authoritative. Not a missing
     * number — a wrong one.
     */
    const cart = withWater()
    const key = cart.lines[0]!.key
    const base = cartSignature(cart)

    expect(
      cartSignature(cartReducer(cart, { type: 'setLineDiscount', key, amount: 0.5 })),
    ).not.toBe(base)
    expect(
      cartSignature(cartReducer(cart, { type: 'setPriceOverride', key, unitPrice: 0.5 })),
    ).not.toBe(base)
    expect(cartSignature(cartReducer(cart, { type: 'setCartDiscount', amount: 0.5 }))).not.toBe(
      base,
    )
  })

  it('distinguishes a line discount from a cart discount of the same amount', () => {
    // They price differently: a cart discount is apportioned across every line,
    // so the tax split differs. One signature for both would serve a stale total.
    const cart = withWater()
    const key = cart.lines[0]!.key

    expect(
      cartSignature(cartReducer(cart, { type: 'setLineDiscount', key, amount: 0.5 })),
    ).not.toBe(cartSignature(cartReducer(cart, { type: 'setCartDiscount', amount: 0.5 })))
  })

  it('forgets everything when the cart is voided', () => {
    const cart = withWater()
    const key = cart.lines[0]!.key

    const adjusted = cartReducer(cartReducer(cart, { type: 'setLineDiscount', key, amount: 0.5 }), {
      type: 'setCartDiscount',
      amount: 1,
    })

    expect(cartReducer(adjusted, { type: 'clear' })).toEqual(EMPTY_CART)
  })
})

/**
 * The sale's identity.
 *
 * CLAUDE.md invariant 6 lives here. The key is minted when tendering begins and
 * reused on every attempt, so a retry after a dropped connection is recognised
 * as the same work. A disabled button is not this mechanism and never was.
 */
describe('the sale key', () => {
  const water: CartProduct = {
    productId: 'p-water',
    name: 'Still Water 500ml',
    sku: 'SKU-1001',
    unit: 'Each',
    unitPrice: 1.2,
  }

  function withWater(): Cart {
    return cartReducer(EMPTY_CART, { type: 'add', product: water })
  }

  it('is absent until a sale begins', () => {
    expect(withWater().saleKey).toBeNull()
  })

  it('is minted once and survives being asked again', () => {
    /*
     * The case that makes it a mechanism rather than a decoration.
     *
     * A cashier enters the tender step, backs out to remove a line, and goes
     * in again. That is one sale tendered twice. If `beginSale` minted afresh,
     * the second attempt would carry a GUID the server had never seen — new
     * work, and the customer pays twice.
     */
    const begun = cartReducer(withWater(), { type: 'beginSale' })

    expect(begun.saleKey).not.toBeNull()
    expect(cartReducer(begun, { type: 'beginSale' }).saleKey).toBe(begun.saleKey)
  })

  it('survives everything that is not the end of the sale', () => {
    const begun = cartReducer(withWater(), { type: 'beginSale' })
    const key = begun.saleKey

    const busy = cartReducer(cartReducer(begun, { type: 'add', product: water }), {
      type: 'setCartDiscount',
      amount: 0.5,
    })

    expect(busy.saleKey).toBe(key)
  })

  it('ends with the cart, so the next customer is a new sale', () => {
    const begun = cartReducer(withWater(), { type: 'beginSale' })

    expect(cartReducer(begun, { type: 'clear' }).saleKey).toBeNull()
  })

  it('stays out of the signature, so entering the tender step does not re-quote', () => {
    // The rule from the other direction: only things that change the price go in
    // the key the quote is cached on. A spinner over the total at the moment the
    // customer is being told it would be the worst possible time for one.
    const cart = withWater()

    expect(cartSignature(cartReducer(cart, { type: 'beginSale' }))).toBe(cartSignature(cart))
  })

  it('is replaced by restartSale, which is the opposite of beginSale and only correct once', () => {
    /*
     * The contrast is the point, which is why this sits next to the idempotence
     * test above rather than in a file of its own.
     *
     * `beginSale` refuses to mint over an existing key because a retry must
     * carry the same one. `restartSale` mints unconditionally, and is reached
     * only from `idempotency-key-reused` — the server saying the key already
     * bought a *different* basket. At that point the old identity belongs to a
     * sale that exists and what is on screen is genuinely new work.
     */
    const begun = cartReducer(withWater(), { type: 'beginSale' })
    const restarted = cartReducer(begun, { type: 'restartSale' })

    expect(restarted.saleKey).not.toBeNull()
    expect(restarted.saleKey).not.toBe(begun.saleKey)

    // And it does not touch the basket: the cashier is looking at those lines.
    expect(restarted.lines).toEqual(begun.lines)
  })
})
