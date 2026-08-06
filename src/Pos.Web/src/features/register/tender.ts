/**
 * The cash arithmetic the tender pad needs, as pure functions.
 *
 * **None of this decides what a customer pays.** The total comes from
 * `POST /sales/quote` and the change comes back from `POST /sales`; what is
 * computed here is (a) *suggestions* for what a person might hand over and
 * (b) a provisional running balance shown while they do it. Both are integer
 * minor units, so CLAUDE.md invariant 3 holds — no `0.1 + 0.2` anywhere near a
 * figure on a screen.
 *
 * The distinction is worth being precise about, because "quick cash" looks like
 * money maths and is not: €20 is not a price, it is a note. The moment an amount
 * becomes something the customer is owed, the server has computed it.
 */

import type { MinorUnits } from '@/lib/money'

/** One amount handed over. `POST /sales` takes an array of these. */
export interface Tender {
  /** Stable across re-renders, so removing the second of two equal notes works. */
  key: string
  amountMinor: MinorUnits
}

/**
 * The notes a shop is likely to be handed, by currency.
 *
 * Only the ones above a typical basket are ever offered, so the ladder does not
 * need to be exhaustive — a €200 note exists and no corner shop wants a button
 * for it. Values in minor units.
 */
const NOTES: Record<string, readonly MinorUnits[]> = {
  EUR: [500, 1000, 2000, 5000],
  GBP: [500, 1000, 2000, 5000],
  USD: [500, 1000, 2000, 5000],
}

const DEFAULT_NOTES: readonly MinorUnits[] = [500, 1000, 2000, 5000]

/** The largest number of quick-cash buttons worth showing at once. */
const MAX_SUGGESTIONS = 4

/**
 * What to offer as one-press amounts for a total.
 *
 * Exact first, because it is the commonest and should be under the thumb that is
 * already there. Then the next whole unit up — the "and a bit" case, £4.60 paid
 * with a fiver — then the notes above it.
 *
 * Duplicates are dropped: on a total of exactly £5.00 the next whole unit *is*
 * the five-pound note, and two identical buttons side by side is a misread
 * waiting to happen.
 */
export function quickCash(totalMinor: MinorUnits, currency: string): MinorUnits[] {
  if (totalMinor <= 0) {
    return []
  }

  const notes = NOTES[currency] ?? DEFAULT_NOTES

  const wholeUnit = Math.ceil(totalMinor / 100) * 100

  const suggestions = [totalMinor, wholeUnit, ...notes.filter((note) => note > totalMinor)]

  return [...new Set(suggestions)].slice(0, MAX_SUGGESTIONS)
}

/** What has been handed over so far. */
export function tenderedMinor(tenders: readonly Tender[]): MinorUnits {
  return tenders.reduce((sum, tender) => sum + tender.amountMinor, 0)
}

/**
 * What is still owed, floored at zero.
 *
 * Floored because the overshoot is *change*, not a negative balance, and the two
 * read very differently at a counter — "remaining −£5.85" invites a cashier to
 * ask for more money.
 */
export function remainingMinor(totalMinor: MinorUnits, tenders: readonly Tender[]): MinorUnits {
  return Math.max(0, totalMinor - tenderedMinor(tenders))
}

/**
 * Change, provisionally.
 *
 * **Not the figure read out loud.** The sale's response carries `changeGiven`,
 * computed by `TenderRules.ChangeFor` from the same amounts the drawer actually
 * took, and that is the authoritative one. This exists so the pad can show
 * something while the cashier is still counting.
 */
export function provisionalChangeMinor(
  totalMinor: MinorUnits,
  tenders: readonly Tender[],
): MinorUnits {
  return Math.max(0, tenderedMinor(tenders) - totalMinor)
}

/** Whether the sale can be completed — the server re-checks regardless. */
export function isCovered(totalMinor: MinorUnits, tenders: readonly Tender[]): boolean {
  return tenders.length > 0 && tenderedMinor(tenders) >= totalMinor
}
