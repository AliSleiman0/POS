import { describe, expect, it } from 'vitest'
import {
  cartReducer,
  cartSignature,
  EMPTY_CART,
  MAX_LINE_QUANTITY,
  provisionalLineMinor,
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
