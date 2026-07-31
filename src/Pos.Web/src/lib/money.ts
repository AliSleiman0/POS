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
 * Formats an amount that came from the server for display.
 *
 * The API sends money as a decimal string (not a JSON number) precisely so it
 * does not pass through a float on the way here. Parsing to a number is safe at
 * the display boundary only: no arithmetic happens after this point.
 */
export function formatMoney(amount: string, currency: string, locale?: string): string {
  const parsed = Number(amount)
  if (!Number.isFinite(parsed)) {
    throw new TypeError(`Not a valid money amount: '${amount}'`)
  }
  return new Intl.NumberFormat(locale, {
    style: 'currency',
    currency,
  }).format(parsed)
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
