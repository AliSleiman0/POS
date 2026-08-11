/**
 * Spreads a cart-level discount across the lines it applies to.
 *
 * A port of `Pos.Core.Pricing.DiscountApportionment`.
 *
 * **Why apportion at all, rather than subtract the discount from the total?**
 * Because tax is per line. A basket holding a zero-rated loaf and a
 * standard-rated bottle, given €5 off, has a different tax total depending on
 * which of the two the €5 came off. Subtracting at the end would have to pick a
 * rate to reduce the tax by, and any single choice is wrong for some basket.
 * Apportioning first means each line is taxed at its own rate on its own
 * discounted amount, and the question never arises.
 *
 * **The parts must sum to exactly the whole.** A discount that apportions to
 * €4.99 of a €5.00 promise is a cent the shop gave away and cannot explain; one
 * that apportions to €5.01 is a cent it took. The remainder step below is what
 * makes the sum exact by construction rather than exact in the cases somebody
 * tried.
 */

import * as money from './money'
import type { Money } from './money'
import { InvalidDiscountError } from './errors'

/**
 * Splits `cartDiscount` across `lineNets` in proportion to each line's net.
 *
 * @param lineNets Each line's amount after its own line discount.
 * @param cartDiscount The amount to spread. Zero is ordinary.
 * @returns One share per line, in order, summing to `cartDiscount` exactly.
 * @throws {InvalidDiscountError} The discount exceeds the basket, or there is
 * nothing for it to apply to.
 */
export function apportion(lineNets: readonly Money[], cartDiscount: Money): readonly Money[] {
  if (money.isNegative(cartDiscount)) {
    throw new InvalidDiscountError('A cart discount cannot be negative.')
  }

  if (money.isZero(cartDiscount)) {
    return lineNets.map(() => money.ZERO)
  }

  const total = money.sum(lineNets)

  if (money.greaterThan(cartDiscount, total)) {
    throw new InvalidDiscountError(
      'A cart discount cannot be larger than the amount it applies to.',
    )
  }

  // Reached when the basket is worth nothing and a discount was still asked for
  // — every line free already, or an empty cart. Proportional shares of zero
  // are undefined, and dividing by the total below would say so with a
  // division error rather than with something a caller can act on.
  if (money.isZero(total)) {
    throw new InvalidDiscountError('There is nothing for a cart discount to apply to.')
  }

  // The whole basket is being comped: each line's share is simply the whole
  // line. Not returned directly, though — it still goes through the rounding
  // pass below, because a line net carries full precision (0.350 kg × 12.99 is
  // 4.5465) and a share is stored at four places. Returning the raw nets here
  // would let the caller round each one independently, and independently
  // rounded parts do not sum to the whole.
  const comped = money.compare(cartDiscount, total) === 0

  const shares: Money[] = []

  for (const net of lineNets) {
    const raw = comped ? net : money.multiply(cartDiscount, money.ratio(net, total))

    // Clamped at the line's own net so a share can never make a line negative,
    // and clamped against the *stored* net because that is what the share will
    // sit beside on the sale line — comparing a rounded share to an unrounded
    // net would let the clamp itself reintroduce full precision.
    const ceiling = money.roundToStorage(net)
    const rounded = money.roundToStorage(raw)

    shares.push(money.greaterThan(rounded, ceiling) ? ceiling : rounded)
  }

  // The remainder is *defined* as what is left over, so the shares plus the
  // remainder are identically the discount given — in either direction, because
  // rounding can have lost or gained. It is at most half a unit in the last
  // place per line, so on any real basket it is a fraction of a cent.
  const remainder = money.subtract(cartDiscount, money.sum(shares))

  if (!money.isZero(remainder)) {
    // Onto the largest line, ties to the lowest index. Largest because it is
    // the line best able to absorb it without the clamp above biting;
    // deterministic because two identical carts must price identically — a
    // quote and the sale that follows it are the same cart, and a customer
    // looking at both should see one number.
    let target = 0

    for (let index = 1; index < lineNets.length; index++) {
      if (money.greaterThan(lineNets[index]!, lineNets[target]!)) {
        target = index
      }
    }

    shares[target] = money.add(shares[target]!, remainder)
  }

  return shares
}
