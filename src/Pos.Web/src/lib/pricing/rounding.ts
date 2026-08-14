/**
 * How this system rounds, and what its numeric columns hold. Stated once.
 *
 * A port of `Pos.Core.Monetary.Rounding`, and the constants are the same
 * constants for the same reasons. The one worth restating: **.NET's default is
 * banker's rounding**, so `decimal.Round(2.5m, 0)` is 2 rather than 3, and a
 * receipt produced that way is one a customer will dispute and be right to.
 * Away-from-zero is stated here and referenced everywhere.
 *
 * Two scales, and the difference between them is CLAUDE.md invariant 3. Line
 * extensions are carried at **full** precision and stored at
 * {@link STORAGE_SCALE}; only the amount a person actually pays is taken to
 * {@link DISPLAY_SCALE}, and only once. Rounding each line to the payable scale
 * and summing gives a total a cent or two from the honest one — the classic
 * penny-off bug, and the reason a Z-report would never balance.
 */

import { round as roundDecimal, type Dec } from './decimal'

/**
 * Scale of the money and quantity columns: `numeric(19,4)`.
 *
 * Four rather than two, so unit prices like `0.1650` are exact and
 * tax-inclusive back-calculation has room to work without accumulating error.
 */
export const STORAGE_SCALE = 4

/**
 * Scale of an amount a person pays: two places, the minor unit of the currency.
 *
 * Distinct from {@link STORAGE_SCALE} on purpose. A sale line is stored at four
 * places; the sale header's total is rounded to this, once, at the end of the
 * pipeline.
 */
export const DISPLAY_SCALE = 2

/** Rounds to `scale` places, half away from zero. The one rounding rule. */
export function to(value: Dec, scale: number): Dec {
  return roundDecimal(value, scale)
}
