/**
 * An amount of the tenant's currency — the type every price, total and tender
 * is expressed in offline.
 *
 * A port of `Pos.Core.Monetary.Money`, and it keeps the property that makes the
 * original worth having: **there is no `add(money, factor)`.** Money plus a
 * bare number does not typecheck, so raw arithmetic on a price has to be
 * written as an explicit conversion, which is visible in a diff. Multiplication
 * and division by a bare decimal *are* offered, because those factors are
 * dimensionless — a quantity, a tax rate, a share — and money times money is
 * meaningless anyway.
 *
 * The brand is what enforces it. `Money` and `Dec` are the same value at
 * runtime and different types at compile time, which is exactly the C# type's
 * relationship to `decimal`.
 *
 * **It does not validate on construction, and that is the point.** Line
 * extensions stay at full precision: 0.3333 kg at €1.2340 is €0.41129220, eight
 * decimals. A type that refused more than four places would force a round on
 * every intermediate value, which *is* the per-line-rounding bug the pipeline
 * exists to prevent. Storability is a boundary question, asked at the point of
 * save by {@link roundToStorage}.
 */

import * as dec from './decimal'
import { DISPLAY_SCALE, STORAGE_SCALE, to } from './rounding'

declare const moneyBrand: unique symbol

/** An amount. A `Dec` the type system will not let you mix with a factor. */
export type Money = dec.Dec & { readonly [moneyBrand]: true }

/** A dimensionless factor — a quantity, a rate, a share. */
export type Factor = dec.Dec

export const ZERO = dec.ZERO as Money

/** The named conversion in. Explicit, like C#'s cast, so the boundary is visible. */
export function money(value: dec.Dec): Money {
  return value as Money
}

/** From the wire's `number | string`. */
export function fromServerDecimal(value: number | string): Money {
  return dec.fromServerDecimal(value) as Money
}

export function add(left: Money, right: Money): Money {
  return dec.add(left, right) as Money
}

export function subtract(left: Money, right: Money): Money {
  return dec.subtract(left, right) as Money
}

export function negate(value: Money): Money {
  return dec.negate(value) as Money
}

/** Scales an amount by a dimensionless factor. */
export function multiply(left: Money, factor: Factor): Money {
  return dec.multiply(left, factor) as Money
}

/** Divides an amount by a dimensionless divisor. */
export function divide(left: Money, divisor: Factor): Money {
  return dec.divide(left, divisor) as Money
}

/**
 * The dimensionless ratio of one amount to another.
 *
 * Money divided by money is a share, not an amount, which is why this is not
 * {@link divide}. It exists for discount apportionment, and it is **the only
 * lossy step in the whole pipeline** — kept at full precision here, with the
 * caller rounding once at the end.
 */
export function ratio(numerator: Money, denominator: Money): Factor {
  return dec.divide(numerator, denominator)
}

export function compare(left: Money, right: Money): number {
  return dec.compare(left, right)
}

export function greaterThan(left: Money, right: Money): boolean {
  return dec.compare(left, right) > 0
}

export function lessThan(left: Money, right: Money): boolean {
  return dec.compare(left, right) < 0
}

export function isZero(value: Money): boolean {
  return dec.isZero(value)
}

export function isNegative(value: Money): boolean {
  return dec.isNegative(value)
}

/**
 * Rounds to the payable scale — the amount a person hands over.
 *
 * Call this **once**, on a total, at the end of a pipeline. Calling it per line
 * and summing is the penny-off bug described in `rounding.ts`.
 */
export function round(value: Money, scale: number = DISPLAY_SCALE): Money {
  return to(value, scale) as Money
}

/** Rounds to what the column holds. The call every writer makes before saving. */
export function roundToStorage(value: Money): Money {
  return to(value, STORAGE_SCALE) as Money
}

/** Adds a sequence of amounts at full precision. */
export function sum(amounts: readonly Money[]): Money {
  let total = ZERO

  for (const amount of amounts) {
    total = add(total, amount)
  }

  return total
}

export function toString(value: Money): string {
  return dec.toString(value)
}
