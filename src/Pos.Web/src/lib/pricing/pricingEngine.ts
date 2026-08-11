/**
 * Turns a cart into the amounts a sale records. Pure: no network, no clock.
 *
 * A port of `Pos.Core.Pricing.PricingEngine`, step for step. The pipeline, in
 * this order and no other:
 *
 * ```
 * line gross (qty × unit price, full precision)
 *   → line discount
 *   → cart discount, apportioned across lines
 *   → tax, on the discounted amount
 *   → header totals, rounded once
 *   → cash rounding
 * ```
 *
 * **Tax comes after both discounts**, never before. Taxing the pre-discount
 * amount charges a customer tax on money they did not spend, and the error
 * scales with the discount.
 *
 * ---
 *
 * **This is the second implementation of these rules, and that is a deliberate
 * amendment to CLAUDE.md invariant 3 rather than a drift from it.** The
 * invariant says the server computes every total and the client displays it,
 * for the excellent reason that two implementations of tax and discount rules
 * will differ eventually — and the place a difference surfaces is a customer
 * disputing a receipt at a counter.
 *
 * Offline there is no server to ask, so the choice is not between one engine
 * and two. It is between two engines and a till that cannot take money when the
 * line goes down, which is the failure Phase 9 exists to fix. What keeps the
 * two honest is `pricing-conformance.json`: a corpus generated from the C#
 * engine's own test cases, asserted by `PricingConformanceTests` on one side
 * and `pricing.conformance.test.ts` on the other. A change to either engine
 * that is not a change to both turns the corpus red in CI.
 *
 * **The online path is unchanged.** `POST /sales/quote` remains authoritative
 * whenever it can be reached; this is consulted only when it cannot.
 */

import { fromInt, type Dec } from './decimal'
import * as money from './money'
import type { Money } from './money'
import { adjustmentFor } from './cashRounding'
import { apportion } from './discountApportionment'
import { InvalidDiscountError } from './errors'
import { netOfTax, taxOn, type TaxMode } from './taxCalculator'

/** One line of a cart, already resolved against the catalog mirror. */
export interface CartLine {
  readonly productId: string
  /** The product's name as of now, for the snapshot and the receipt. */
  readonly description: string
  /** How many, or how much. Weighed goods are ordinary — 0.350 kg of cheese. */
  readonly quantity: Dec
  /** The catalog price, or a manager's override. The engine cannot tell. */
  readonly unitPrice: Money
  /** The tax class's rate as a fraction: `0.2300` is 23%. Per line, not per cart. */
  readonly taxRate: Dec
  /** An absolute amount off this line, never a percentage. */
  readonly lineDiscount: Money
  readonly isPriceOverridden?: boolean
}

/** Everything the engine needs, and nothing it does not. */
export interface Cart {
  readonly lines: readonly CartLine[]
  /** An absolute amount off the whole basket, apportioned across the lines. */
  readonly cartDiscount: Money
  readonly taxMode: TaxMode
  /** The tenant's smallest coin, or zero. Applied last, to the payable total. */
  readonly cashRoundingIncrement: Dec
}

/** One priced line, as the register displays and the receipt prints it. */
export interface PricedLine {
  readonly source: CartLine
  readonly lineNumber: number
  readonly gross: Money
  readonly subtotal: Money
  readonly discount: Money
  readonly cartDiscountShare: Money
  readonly tax: Money
  readonly total: Money
}

/** A priced cart: the amounts a sale records. */
export interface PricedSale {
  readonly lines: readonly PricedLine[]
  readonly subtotal: Money
  readonly discountTotal: Money
  readonly taxTotal: Money
  readonly roundingAdjustment: Money
  readonly total: Money
}

const ZERO_RATE = fromInt(0)

/**
 * Prices `cart`.
 *
 * @throws {InvalidDiscountError} A discount exceeds what it applies to.
 */
export function price(cart: Cart): PricedSale {
  // 1. Gross and the line's own discount, at full precision. A zero-quantity
  //    line, a zero-price line and a fully-discounted line are all priced
  //    without complaint — a free gift is a real line. The caller refuses an
  //    empty cart and a zero quantity as UI slips, which is a different
  //    question from whether they compute.
  const gross: Money[] = []
  const nets: Money[] = []

  for (const [index, line] of cart.lines.entries()) {
    if (money.isNegative(line.lineDiscount)) {
      throw new InvalidDiscountError(`Line ${String(index + 1)} carries a negative discount.`)
    }

    const lineGross = money.multiply(line.unitPrice, line.quantity)

    if (money.greaterThan(line.lineDiscount, lineGross)) {
      throw new InvalidDiscountError(
        `Line ${String(index + 1)}'s discount is larger than the line.`,
      )
    }

    gross.push(lineGross)
    nets.push(money.subtract(lineGross, line.lineDiscount))
  }

  // 2. The cart discount, spread over the lines.
  const shares = apportion(nets, cart.cartDiscount)

  // 3. Tax per line, on what is left after both discounts.
  const priced: PricedLine[] = []

  let payable = money.ZERO
  let taxTotal = money.ZERO
  let discountTotal = money.ZERO

  for (const [index, line] of cart.lines.entries()) {
    const taxable = money.subtract(nets[index]!, shares[index]!)
    const tax = taxOn(taxable, line.taxRate, cart.taxMode)

    // What this line contributes to the amount payable. In Exclusive mode tax
    // is added; in Inclusive mode the discounted price already contains it.
    const lineTotal = cart.taxMode === 'Exclusive' ? money.add(taxable, tax) : taxable

    // Subtotal and discount are both recorded net of tax, in both modes, so
    // that the sale-level identity holds however the tenant prices. In
    // Inclusive mode that means dividing the tax back out of each.
    const subtotal = netOfTax(gross[index]!, line.taxRate, cart.taxMode)
    const discount = netOfTax(
      money.add(line.lineDiscount, shares[index]!),
      line.taxRate,
      cart.taxMode,
    )

    priced.push({
      source: line,
      lineNumber: index + 1,
      gross: money.roundToStorage(gross[index]!),
      subtotal: money.roundToStorage(subtotal),
      discount: money.roundToStorage(discount),
      cartDiscountShare: money.roundToStorage(shares[index]!),
      tax: money.roundToStorage(tax),
      total: money.roundToStorage(lineTotal),
    })

    // Accumulated at FULL precision, from the unrounded values — not from the
    // rounded ones just stored. Summing the rounded lines is the penny-off bug.
    payable = money.add(payable, lineTotal)
    taxTotal = money.add(taxTotal, tax)
    discountTotal = money.add(discountTotal, discount)
  }

  // 4. Rounded once, here, and nowhere else.
  const payableRounded = money.round(payable)
  const taxRounded = money.round(taxTotal)
  const discountRounded = money.round(discountTotal)

  /*
   * Subtotal is DERIVED, not independently summed, and this is the deliberate
   * call in the whole pipeline. DATA-MODEL.md invariant 1 says
   *     Total == Subtotal − DiscountTotal + TaxTotal + RoundingAdjustment
   * must hold on the STORED two-decimal values, in both tax modes. Four
   * independently rounded sums cannot guarantee that: each can be up to half a
   * cent out, and the identity then fails by a cent on some baskets and not
   * others.
   *
   * One of the four has to absorb the residue, and Subtotal is the right one
   * because it is the only one never reconciled against anything outside the
   * system — TaxTotal against a VAT return, DiscountTotal against a promotions
   * report, Total against the cash in the drawer. It also stays within a cent
   * of the natural sum, so nothing is being distorted to make the arithmetic
   * work.
   */
  const subtotalRounded = money.subtract(money.add(payableRounded, discountRounded), taxRounded)

  // 5. Cash rounding, last, on the payable total only. Recorded, never absorbed.
  const adjustment = adjustmentFor(payableRounded, cart.cashRoundingIncrement)

  return {
    lines: priced,
    subtotal: subtotalRounded,
    discountTotal: discountRounded,
    taxTotal: taxRounded,
    roundingAdjustment: adjustment,
    total: money.add(payableRounded, adjustment),
  }
}

export { ZERO_RATE }
