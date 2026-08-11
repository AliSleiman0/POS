/**
 * What has to be true about the money offered against a sale, and how much goes
 * back.
 *
 * A port of `Pos.Core.Tenders.TenderRules`, and separate from the pricing
 * engine for the same reason the original is: pricing decides what is owed,
 * this decides whether it has been paid. The two fail for different reasons and
 * a cashier acts on them differently — one is "that discount is wrong", the
 * other is "I need another note".
 *
 * Only the two the offline register needs are ported. `IsAccepted` and
 * `IsSignConsistent` are server-side validation of a client's request, and the
 * offline till is the client — re-implementing them here would be checking its
 * own homework.
 */

import * as money from './money'
import type { Money } from './money'

/** The tenders do not cover the total. Mirrors the server's `under-tender`. */
export class UnderTenderError extends Error {
  constructor(message: string) {
    super(message)
    this.name = 'UnderTenderError'
  }
}

/**
 * Change due on a sale: the excess over `total`.
 *
 * @throws {UnderTenderError} The tenders do not cover the total.
 */
export function changeFor(total: Money, tenders: readonly Money[]): Money {
  const offered = money.sum(tenders)

  if (money.lessThan(offered, total)) {
    throw new UnderTenderError(
      `The sale comes to ${money.toString(total)} and ${money.toString(offered)} was tendered.`,
    )
  }

  // Over-tender is the normal case, not an exception to handle: a customer pays
  // for 18.45 with a 20 note far more often than they produce exact change.
  return money.subtract(offered, total)
}

/**
 * Whether `tenders` cover `total`.
 *
 * Offered alongside {@link changeFor} so the tender pad can enable or disable
 * its Complete button without catching — the same reason the C# offers both.
 */
export function isSufficient(total: Money, tenders: readonly Money[]): boolean {
  return !money.lessThan(money.sum(tenders), total)
}
