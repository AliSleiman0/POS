/**
 * The offline engine's own behaviour, beyond agreeing with the corpus.
 *
 * The corpus proves the two implementations produce the same amounts. It cannot
 * prove the things it deliberately excludes: the carts the engine **refuses**,
 * which are skipped when the fixture is generated because a file of expected
 * amounts is the wrong place to record an error.
 *
 * Those refusals matter offline more than they do online. A basket the server
 * would reject is one a cashier can otherwise complete offline and only
 * discover as a permanent sync failure hours later — with the money already in
 * the drawer and the customer long gone.
 */

import { describe, expect, it } from 'vitest'
import { parse, toString } from './decimal'
import { money, ZERO } from './money'
import { changeFor, isSufficient, UnderTenderError } from './tenderRules'
import { InvalidDiscountError } from './errors'
import { price, type Cart, type CartLine } from './pricingEngine'

function line(overrides: Partial<CartLine> = {}): CartLine {
  return {
    productId: 'p1',
    description: 'Water',
    quantity: parse('1'),
    unitPrice: money(parse('1.2000')),
    taxRate: parse('0.2300'),
    lineDiscount: ZERO,
    ...overrides,
  }
}

function cart(overrides: Partial<Cart> = {}): Cart {
  return {
    lines: [line()],
    cartDiscount: ZERO,
    taxMode: 'Inclusive',
    cashRoundingIncrement: parse('0'),
    ...overrides,
  }
}

describe('the sale Phase 8 rang against production', () => {
  it('prices €1.20 at 23% inclusive as €0.98 + €0.22', () => {
    // The one number in this file that a person has actually seen on a receipt.
    const sale = price(cart())

    expect(toString(sale.total)).toBe('1.20')
    expect(toString(sale.taxTotal)).toBe('0.22')
    expect(toString(sale.subtotal)).toBe('0.98')
  })
})

describe('refusals — the carts the server would also reject', () => {
  it('refuses a line discount larger than its line', () => {
    expect(() => price(cart({ lines: [line({ lineDiscount: money(parse('2.0000')) })] }))).toThrow(
      InvalidDiscountError,
    )
  })

  it('refuses a negative line discount', () => {
    expect(() => price(cart({ lines: [line({ lineDiscount: money(parse('-0.5000')) })] }))).toThrow(
      InvalidDiscountError,
    )
  })

  it('refuses a cart discount larger than the basket', () => {
    expect(() => price(cart({ cartDiscount: money(parse('99.0000')) }))).toThrow(
      InvalidDiscountError,
    )
  })

  it('refuses a cart discount on a basket worth nothing', () => {
    // Every line already free and a discount still asked for. Proportional
    // shares of zero are undefined, and the message says so rather than the
    // engine dividing by zero.
    expect(() =>
      price(
        cart({
          lines: [line({ unitPrice: ZERO })],
          cartDiscount: money(parse('1.0000')),
        }),
      ),
    ).toThrow(InvalidDiscountError)
  })

  it('names the line, so a cashier is told which one', () => {
    expect(() =>
      price(
        cart({
          lines: [line(), line({ lineDiscount: money(parse('9.0000')) })],
        }),
      ),
    ).toThrow(/Line 2/)
  })
})

describe('the cases that are ordinary rather than edge', () => {
  it('prices a free gift without complaint', () => {
    const sale = price(cart({ lines: [line({ unitPrice: ZERO }), line({ productId: 'p2' })] }))

    expect(toString(sale.total)).toBe('1.20')

    // Zero, without pinning the scale it arrives at. The scale of a zero
    // depends on the tax mode — inclusive multiplies by a 28-place factor and
    // collapses to `0`, exclusive by a 4-place rate and gives `0.0000` — and it
    // is the *corpus* that governs which, on both engines at once. Asserting a
    // literal here would duplicate that and be wrong half the time.
    expect(sale.lines[0]!.total.m).toBe(0n)
  })

  it('prices a weighed line', () => {
    const sale = price(
      cart({
        lines: [line({ quantity: parse('0.350'), unitPrice: money(parse('12.9900')) })],
      }),
    )

    // 0.350 × 12.9900 = 4.5465 at full precision, rounded once at the header.
    expect(toString(sale.lines[0]!.gross)).toBe('4.5465')
    expect(toString(sale.total)).toBe('4.55')
  })

  it('applies cash rounding as a recorded adjustment, never absorbed', () => {
    const sale = price(
      cart({
        lines: [line({ unitPrice: money(parse('4.9700')), taxRate: parse('0') })],
        taxMode: 'Exclusive',
        cashRoundingIncrement: parse('0.05'),
      }),
    )

    expect(toString(sale.total)).toBe('4.95')
    expect(toString(sale.roundingAdjustment)).toBe('-0.02')

    // And invariant 1 still holds on the stored values, which is the whole
    // reason the adjustment is a column rather than a nudge.
    const identity =
      Number(toString(sale.subtotal)) -
      Number(toString(sale.discountTotal)) +
      Number(toString(sale.taxTotal)) +
      Number(toString(sale.roundingAdjustment))

    expect(identity.toFixed(2)).toBe(toString(sale.total))
  })

  it('numbers the lines from one, in cart order', () => {
    const sale = price(
      cart({ lines: [line(), line({ productId: 'p2' }), line({ productId: 'p3' })] }),
    )

    expect(sale.lines.map((l) => l.lineNumber)).toEqual([1, 2, 3])
  })
})

describe('tender rules', () => {
  it('gives change on an over-tender, which is the normal case', () => {
    expect(toString(changeFor(money(parse('1.20')), [money(parse('2.00'))]))).toBe('0.80')
  })

  it('sums split tenders', () => {
    expect(
      toString(changeFor(money(parse('18.45')), [money(parse('10.00')), money(parse('10.00'))])),
    ).toBe('1.55')
  })

  it('refuses an under-tender rather than inventing a negative change', () => {
    expect(() => changeFor(money(parse('5.00')), [money(parse('2.00'))])).toThrow(UnderTenderError)
  })

  it('treats exact change as sufficient', () => {
    expect(isSufficient(money(parse('1.20')), [money(parse('1.20'))])).toBe(true)
    expect(toString(changeFor(money(parse('1.20')), [money(parse('1.20'))]))).toBe('0.00')
  })

  it('answers sufficiency without throwing, so a button can be disabled', () => {
    expect(isSufficient(money(parse('5.00')), [money(parse('2.00'))])).toBe(false)
  })
})
