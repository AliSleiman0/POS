/**
 * Rounding a payable total to the smallest coin that actually exists.
 *
 * A port of `Pos.Core.Monetary.CashRounding`. Some jurisdictions have withdrawn
 * their 1c and 2c coins, so a cash total of €4.97 is paid as €4.95. This is
 * **not** the same operation as rounding to the currency's minor unit: that one
 * takes a full-precision amount to two places, this one takes an already-exact
 * amount to a coarser increment because of what is in the till.
 *
 * **The difference is recorded, never absorbed.** The pipeline stores it as
 * `Sale.RoundingAdjustment`, so `Subtotal − DiscountTotal + TaxTotal +
 * RoundingAdjustment == Total` still holds exactly. Nudging the total instead
 * would leave the drawer over or short by an amount nothing in the system
 * explains — precisely the discrepancy a Z-report exists to surface.
 *
 * An increment of zero means the jurisdiction has no such rule, which is the
 * default and the overwhelmingly common case. It is an exact no-op rather than
 * a divide by zero.
 */

import { divide, isZero, isNegative, multiply, type Dec } from './decimal'
import * as money from './money'
import type { Money } from './money'
import { STORAGE_SCALE, to } from './rounding'

/**
 * Rounds `total` to the nearest multiple of `increment`.
 *
 * @param total The payable amount, already at the currency's minor unit.
 * @param increment The smallest coin, e.g. `0.05`. Zero means none applies.
 */
export function toIncrement(total: Money, increment: Dec): Money {
  if (isZero(increment) || isNegative(increment)) {
    return total
  }

  const steps = to(divide(total, increment), 0)

  // Back to the storage scale, because steps × increment can carry more places
  // than either operand — 0.05 × 3 is exact, but an increment like 0.025 would
  // not be.
  return money.money(to(multiply(steps, increment), STORAGE_SCALE))
}

/**
 * What {@link toIncrement} would add to `total`: the value stored as
 * `Sale.RoundingAdjustment`.
 *
 * Signed, and either sign is ordinary — €4.97 rounds down by 2c, €4.98 rounds
 * up by 2c.
 */
export function adjustmentFor(total: Money, increment: Dec): Money {
  return money.subtract(toIncrement(total, increment), total)
}
