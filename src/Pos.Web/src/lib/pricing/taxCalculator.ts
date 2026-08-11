/**
 * Tax on one line's discounted amount, in whichever direction the tenant prices.
 *
 * A port of `Pos.Core.Pricing.TaxCalculator`. The two modes are not two ways of
 * displaying one number — they are two different meanings for the same stored
 * price, which is why `TaxMode` is fixed at onboarding and refused afterwards.
 * `Exclusive`: the shelf says €10 and the customer pays €12.30. `Inclusive`:
 * the shelf says €10 and the customer pays €10, of which €1.87 is tax.
 *
 * **Nothing here rounds.** Every value comes back at full precision and the
 * pipeline rounds once, at the header.
 */

import { add, divide, fromInt, type Dec } from './decimal'
import * as money from './money'
import type { Money } from './money'

/** The tenant's tax mode. Matches the API's `TaxMode` enum by name. */
export type TaxMode = 'Inclusive' | 'Exclusive'

const ONE = fromInt(1)

/**
 * Tax contained in, or to be added to, `taxable`.
 *
 * @param taxable The amount after every discount. Tax is never on the gross.
 * @param rate The rate as a fraction: `0.2300` is 23%.
 * @param mode How `taxable` should be read.
 */
export function taxOn(taxable: Money, rate: Dec, mode: TaxMode): Money {
  if (mode === 'Exclusive') {
    // Added on top: the price did not contain it.
    return money.multiply(taxable, rate)
  }

  /*
   * Extracted from within: gross × rate / (1 + rate).
   *
   * **The order of operations is load-bearing, not stylistic.** The C# reads
   * `taxable * (rate / (1m + rate))` — divide first, multiply second. Written
   * the other way round, `(taxable * rate) / (1m + rate)`, the rounding of a
   * non-terminating quotient happens at a different point in the expression and
   * the results differ in the last place on ordinary baskets. This mirrors the
   * C# exactly, and the conformance corpus is what proves it still does.
   *
   * At a rate of 1 — legal, if degenerate — the divisor is 2 and exactly half
   * the shelf price is tax, which is the right answer rather than an edge case
   * to guard.
   */
  return money.multiply(taxable, divide(rate, add(ONE, rate)))
}

/**
 * The net-of-tax value of `amount` — what it contributes to `Sale.Subtotal`.
 *
 * In `Exclusive` mode a stored price is already net, so this is the identity.
 * In `Inclusive` mode it has to be divided out, which is the whole reason
 * `Sale.Subtotal` cannot simply be the sum of quantity times price.
 */
export function netOfTax(amount: Money, rate: Dec, mode: TaxMode): Money {
  return mode === 'Exclusive' ? amount : money.divide(amount, add(ONE, rate))
}
