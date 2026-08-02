/**
 * Money handling for the client.
 *
 * The rule (CLAUDE.md invariant 3): money is NEVER a JS `number` in a
 * calculation. `0.1 + 0.2 !== 0.3` in IEEE-754, which is not acceptable at a
 * till. The server computes every total; the client displays what it is given.
 *
 * Where a provisional subtotal must be shown before the server's quote returns,
 * it is computed in integer minor units (cents) and clearly marked as
 * non-authoritative.
 */

/** An amount in minor units — 1234 is £12.34. Always an integer. */
export type MinorUnits = number

/**
 * A `decimal` as the generated client types it.
 *
 * The API's DTOs are `decimal` (`SaleContractTests` fails the backend build if
 * one ever declares a `Money`), and .NET's OpenAPI describes a decimal as
 * `type: ["number", "string"]` — so `schema.d.ts` gives every amount, rate and
 * quantity as `number | string`. In practice System.Text.Json always writes a
 * number, but the declared contract permits both and the union is not worth
 * hand-narrowing in a generated file.
 *
 * This module is the single place that resolves it. Nothing else in the app
 * should touch the raw union.
 */
export type ServerDecimal = number | string

/**
 * Resolves a server decimal to a `number`, **for display only**.
 *
 * CLAUDE.md invariant 3 is unaffected by this: the prohibition is on
 * *arithmetic*, not on rendering. Every total is computed server-side; this
 * converts the answer into something `Intl.NumberFormat` accepts and nothing
 * else happens to it afterwards.
 *
 * A string is parsed strictly rather than with `Number()`, which cheerfully
 * accepts `''`, `'0x10'` and whitespace. The pattern is the one the OpenAPI
 * document itself declares for a decimal.
 */
export function parseServerDecimal(value: ServerDecimal): number {
  if (typeof value === 'number') {
    if (!Number.isFinite(value)) {
      throw new TypeError(`Not a valid decimal: ${String(value)}`)
    }
    return value
  }

  if (!/^-?(?:0|[1-9]\d*)(?:\.\d+)?$/.test(value)) {
    throw new TypeError(`Not a valid decimal: '${value}'`)
  }

  return Number(value)
}

/**
 * Formats an amount that came from the server for display.
 *
 * Formatting is the display boundary and nothing may happen after it. Anything
 * that needs to *add* amounts either asks the server (`POST /sales/quote`) or
 * uses the integer minor-unit helpers below.
 */
export function formatMoney(amount: ServerDecimal, currency: string, locale?: string): string {
  return new Intl.NumberFormat(locale, {
    style: 'currency',
    currency,
  }).format(parseServerDecimal(amount))
}

/**
 * Formats a quantity — a `decimal` too, because a weighed line is 0.35 kg.
 *
 * Up to three decimals and no trailing zeros, so `2` reads as "2" rather than
 * "2.000" on a till display that is mostly whole numbers.
 */
export function formatQuantity(quantity: ServerDecimal, locale?: string): string {
  return new Intl.NumberFormat(locale, {
    maximumFractionDigits: 3,
  }).format(parseServerDecimal(quantity))
}

/** Formats a tax rate. The server stores `0.2` and a human reads "20%". */
export function formatRate(rate: ServerDecimal, locale?: string): string {
  return new Intl.NumberFormat(locale, {
    style: 'percent',
    maximumFractionDigits: 2,
  }).format(parseServerDecimal(rate))
}

/** Formats an integer minor-unit amount. Use for provisional client-side subtotals. */
export function formatMinorUnits(amount: MinorUnits, currency: string, locale?: string): string {
  if (!Number.isInteger(amount)) {
    throw new TypeError(`Minor units must be an integer, got ${amount}`)
  }
  return new Intl.NumberFormat(locale, {
    style: 'currency',
    currency,
  }).format(amount / 100)
}

/**
 * Provisional line total in minor units. Integer arithmetic only.
 *
 * NOT authoritative — the server's `POST /sales/quote` is. This exists so the
 * cart can show something during the round trip, never to decide what a
 * customer pays.
 */
export function provisionalLineTotal(unitPriceMinor: MinorUnits, quantity: number): MinorUnits {
  if (!Number.isInteger(unitPriceMinor)) {
    throw new TypeError(`Unit price must be integer minor units, got ${unitPriceMinor}`)
  }
  return Math.round(unitPriceMinor * quantity)
}

/** Provisional cart subtotal in minor units. Same caveat as above. */
export function provisionalSubtotal(lines: readonly MinorUnits[]): MinorUnits {
  return lines.reduce((sum, line) => sum + line, 0)
}
