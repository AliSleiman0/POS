/**
 * C#'s `decimal`, in TypeScript.
 *
 * **Why this file exists at all.** Offline, there is no `POST /sales/quote` to
 * ask, so the register has to price its own cart — and the amounts it produces
 * are what a customer is charged and what the receipt says. When the sale
 * finally syncs, the server re-prices nothing: it stores the snapshots. So the
 * two engines have to agree *exactly*, and "exactly" means at the last of four
 * decimal places, in a pipeline whose one lossy step is a division.
 *
 * A `number` cannot do this. `0.1 + 0.2 !== 0.3` is the famous part; the part
 * that matters here is that binary floating point cannot represent a tenth at
 * all, so a total assembled from prices would drift from the server's in a way
 * that shows up as a penny on some baskets and not others — the worst shape a
 * money bug has, because it looks like nothing until a customer disputes it.
 *
 * So this is a faithful port of the .NET type, not an approximation of it:
 *
 * - **A scaled integer**, `value = mantissa / 10ⁿ`, with the mantissa held as a
 *   `bigint` and bounded by 96 bits exactly as .NET bounds it, and the scale
 *   bounded at 28.
 * - **`+`, `−` and `×` are exact** while they fit. Addition aligns to the wider
 *   scale; multiplication sums the scales.
 * - **`÷` is the only lossy operation**, and it loses precision the same way
 *   .NET does: the exact quotient when it terminates, otherwise as many digits
 *   as a 96-bit mantissa holds, **rounded half-to-even** at the cut.
 *
 * Every one of those behaviours was established by running the real thing
 * rather than by reading about it — see `pricing-conformance.json`, which both
 * this engine and `PricingEngine.Price` are asserted against.
 *
 * **Not a general-purpose decimal library.** It implements what the pricing
 * pipeline performs and nothing else, and it is deliberately not exported from
 * the app's public surface: money still arrives from the server, and this is
 * consulted only when the server cannot be reached.
 */

/**
 * `2⁹⁶ − 1` — the largest mantissa .NET's `decimal` holds.
 *
 * Not a round number and not arbitrary: it is what three 32-bit words hold, and
 * reproducing the limit is what makes the two implementations agree on the
 * handful of carts where precision actually runs out.
 */
export const MAX_MANTISSA = (1n << 96n) - 1n

/** The most decimal places .NET's `decimal` carries. */
export const MAX_SCALE = 28

/**
 * A decimal number: `sign * mantissa / 10^scale`.
 *
 * The mantissa is **signed**, unlike .NET's sign-and-magnitude representation.
 * That is the one deliberate divergence, and it is invisible: `bigint` division
 * truncates toward zero in the same direction .NET's magnitude arithmetic does,
 * so every rounding decision below lands identically. Holding a separate sign
 * would mean writing `-0`, which JavaScript would then compare unequal to `0`
 * in some places and equal in others.
 */
export interface Dec {
  readonly m: bigint
  readonly s: number
}

export const ZERO: Dec = { m: 0n, s: 0 }

const ONE: Dec = { m: 1n, s: 0 }

/** `10ⁿ`, memoised — the inner loop of every operation here. */
const POWERS: bigint[] = []

function pow10(n: number): bigint {
  if (n < 0) {
    throw new RangeError(`10^${String(n)} is not a scaling factor.`)
  }

  for (let i = POWERS.length; i <= n; i++) {
    POWERS[i] = i === 0 ? 1n : POWERS[i - 1]! * 10n
  }

  return POWERS[n]!
}

function abs(value: bigint): bigint {
  return value < 0n ? -value : value
}

/**
 * Divides, rounding half to even.
 *
 * **The rounding mode is not a choice.** It is what .NET's `decimal` does when
 * it runs out of room, established by probing the real type: `2.5e-28 / 1`
 * lands on `2e-28`, `3.5e-28` on `4e-28`, `1.5e-28` on `2e-28`. Using
 * away-from-zero here — which is what the *pipeline* uses, one level up —
 * would disagree with the server on exactly the carts where a division does not
 * terminate, which is most carts carrying a cart discount.
 */
function divideHalfEven(numerator: bigint, denominator: bigint): bigint {
  const negative = numerator < 0n !== denominator < 0n
  const n = abs(numerator)
  const d = abs(denominator)

  const quotient = n / d
  const remainder = n % d
  const twice = remainder * 2n

  let rounded = quotient

  if (twice > d || (twice === d && quotient % 2n === 1n)) {
    rounded = quotient + 1n
  }

  return negative ? -rounded : rounded
}

/** Divides, rounding half away from zero — the pipeline's own rule. */
function divideHalfAwayFromZero(numerator: bigint, denominator: bigint): bigint {
  const negative = numerator < 0n !== denominator < 0n
  const n = abs(numerator)
  const d = abs(denominator)

  const quotient = n / d
  const twice = (n % d) * 2n
  const rounded = twice >= d ? quotient + 1n : quotient

  return negative ? -rounded : rounded
}

/**
 * Brings a raw `(mantissa, scale)` back inside the representable range by
 * dropping low-order digits, half-to-even.
 *
 * This is where `+`, `−` and `×` stop being exact — and only there. .NET does
 * the same thing and calls it nothing; the effect is visible when a
 * multiplication's scales sum past 28, which in this pipeline means a price
 * times a quantity times a tax ratio.
 */
function normalize(m: bigint, s: number): Dec {
  // Inside the range already: return it untouched, trailing zeros and all.
  // `0.0000 × 1` is `0.0000`, not `0` — .NET only sheds zeros when it has to.
  if (s <= MAX_SCALE && abs(m) <= MAX_MANTISSA) {
    return { m, s }
  }

  /*
   * An exact zero that has to be reduced collapses to no decimal places at all.
   *
   * .NET's own special case, and it is observable rather than academic:
   * `0.0m × 0.047…` (28 places) is `0` at scale **0**, while `1e-28 × 0.1` —
   * which *rounds* to zero from a nonzero mantissa — is `0` at scale **28**.
   * The distinction is the mantissa on the way in, not the one on the way out.
   *
   * It matters because a fully-comped inclusive line has a zero taxable amount,
   * so its tax is exactly this multiplication. Getting it wrong made the port
   * report `0.0000` where the server reported `0` — the same number, a
   * different result, and the corpus compares scales.
   */
  if (m === 0n) {
    return { m: 0n, s: 0 }
  }

  let mantissa = m
  let scale = s

  /*
   * Shed trailing zeros, but only as many as it takes to get back in range.
   *
   * Read off .NET: `1e-27` produced as mantissa 100 at scale 29 comes back as
   * mantissa 10 at scale 28, not mantissa 1 at scale 27. It sheds the digit it
   * needs and stops, and it sheds before rounding so an exact value is not
   * rounded away when it did not have to be.
   */
  while (
    scale > 0 &&
    mantissa % 10n === 0n &&
    (scale > MAX_SCALE || abs(mantissa) > MAX_MANTISSA)
  ) {
    mantissa /= 10n
    scale -= 1
  }

  // Scale still over the limit: drop the excess digits in one rounding step
  // rather than one at a time, which would round repeatedly and drift.
  if (scale > MAX_SCALE) {
    mantissa = divideHalfEven(mantissa, pow10(scale - MAX_SCALE))
    scale = MAX_SCALE
  }

  // Mantissa over the limit: keep dropping until it fits.
  while (abs(mantissa) > MAX_MANTISSA) {
    if (scale === 0) {
      throw new RangeError('Decimal overflow: the value is too large to represent.')
    }

    mantissa = divideHalfEven(mantissa, 10n)
    scale -= 1
  }

  return { m: mantissa, s: scale }
}

/** Raises `value` to `scale` places without rounding. The caller guarantees it fits. */
function extend(value: Dec, scale: number): bigint {
  return value.m * pow10(scale - value.s)
}

export function add(a: Dec, b: Dec): Dec {
  const scale = Math.max(a.s, b.s)

  return normalize(extend(a, scale) + extend(b, scale), scale)
}

export function subtract(a: Dec, b: Dec): Dec {
  const scale = Math.max(a.s, b.s)

  return normalize(extend(a, scale) - extend(b, scale), scale)
}

export function negate(value: Dec): Dec {
  return { m: -value.m, s: value.s }
}

/** Exact while it fits: the scales add, as they do in .NET. */
export function multiply(a: Dec, b: Dec): Dec {
  return normalize(a.m * b.m, a.s + b.s)
}

/**
 * Division, reproducing .NET's result *scale* as well as its digits.
 *
 * The scale is the part that is easy to get wrong and impossible to notice,
 * because two decimals of different scale compare equal — `2.5` and `2.5000`
 * are the same number. It matters anyway: the scale propagates through the
 * multiplication that follows, and the pipeline rounds only at the end, so a
 * quotient carrying the wrong number of places can move the last digit of a
 * total. .NET's rule, established by probing it:
 *
 * - The **preferred scale** is the dividend's minus the divisor's, floored at
 *   zero. `1.0000 / 1` is `1.0000`, not `1`.
 * - An **exact** quotient uses the smallest scale that represents it, but never
 *   fewer places than preferred. `10 / 4` is `2.5`; `2.5000 / 5` is `0.5000`.
 * - An **inexact** quotient takes as many places as a 96-bit mantissa holds, up
 *   to 28, rounded half-to-even. `1 / 3` has 28 places; `100 / 3` has 27,
 *   because 28 would not fit.
 *
 * @throws {RangeError} when dividing by zero.
 */
export function divide(a: Dec, b: Dec): Dec {
  if (b.m === 0n) {
    throw new RangeError('Division by zero.')
  }

  const preferred = Math.min(Math.max(a.s - b.s, 0), MAX_SCALE)

  // A zero dividend still takes the preferred scale rather than collapsing to
  // zero places: `0.0000 / 1` is `0.0000`, `0.0000 / 1.05` is `0.00`, and
  // `0.0000 / 1.0500` is `0`. Returning a bare zero here made a comped line's
  // net-of-tax disagree with the server in scale alone.
  if (a.m === 0n) {
    return { m: 0n, s: preferred }
  }

  // a/b = (a.m / 10^a.s) / (b.m / 10^b.s) = (a.m * 10^b.s) / (b.m * 10^a.s).
  const numerator = a.m * pow10(b.s)
  const denominator = b.m * pow10(a.s)

  for (let scale = MAX_SCALE; scale >= 0; scale--) {
    const scaled = numerator * pow10(scale)
    const quotient = scaled / denominator

    if (scaled % denominator === 0n) {
      if (abs(quotient) > MAX_MANTISSA) {
        // Exact, but too many digits to hold at this scale. Fewer places may
        // still be exact, so keep walking down rather than giving up.
        continue
      }

      // Exact: shed the trailing zeros this loop's starting scale introduced,
      // but never drop below the preferred scale.
      let m = quotient
      let s = scale

      while (s > preferred && m % 10n === 0n) {
        m /= 10n
        s -= 1
      }

      return { m, s }
    }

    const rounded = divideHalfEven(scaled, denominator)

    if (abs(rounded) <= MAX_MANTISSA) {
      return { m: rounded, s: scale }
    }
  }

  throw new RangeError('Decimal overflow: the quotient is too large to represent.')
}

export function compare(a: Dec, b: Dec): number {
  const scale = Math.max(a.s, b.s)
  const left = extend(a, scale)
  const right = extend(b, scale)

  return left < right ? -1 : left > right ? 1 : 0
}

export function isZero(value: Dec): boolean {
  return value.m === 0n
}

export function isNegative(value: Dec): boolean {
  return value.m < 0n
}

/**
 * Rounds to `scale` places, half **away from zero**.
 *
 * The pipeline's own rule, and distinct from the half-to-even used above when
 * precision runs out. Away-from-zero is the rounding a shopper expects in both
 * directions; .NET's default is banker's rounding, and a receipt produced that
 * way is one a customer will dispute and be right to.
 *
 * A value already at or below `scale` is returned **unchanged rather than
 * padded**, matching `decimal.Round`: `Round(2.5, 4)` is `2.5`, not `2.5000`.
 */
export function round(value: Dec, scale: number): Dec {
  if (value.s <= scale) {
    return value
  }

  return {
    m: divideHalfAwayFromZero(value.m, pow10(value.s - scale)),
    s: scale,
  }
}

/**
 * Parses the decimal literal in `text`.
 *
 * Accepts an optional sign, digits with an optional point, and an optional
 * exponent — the last because `String(1e-7)` is `"1e-7"`, and a value that
 * arrived as a JSON number can be handed here already in that form.
 *
 * @throws {RangeError} on anything it does not fully recognise. Deliberately
 * strict: a silently-zero price is worse than a thrown error, because the till
 * would sell the item for nothing and report success.
 */
export function parse(text: string): Dec {
  const match = /^([+-]?)(\d*)(?:\.(\d*))?(?:[eE]([+-]?\d+))?$/.exec(text.trim())

  if (match === null) {
    throw new RangeError(`"${text}" is not a decimal.`)
  }

  const [, sign, whole = '', fraction = '', exponent] = match

  if (whole.length === 0 && fraction.length === 0) {
    throw new RangeError(`"${text}" is not a decimal.`)
  }

  let m = BigInt(`${whole}${fraction}` === '' ? '0' : `${whole}${fraction}`)
  let s = fraction.length

  if (exponent !== undefined) {
    const shift = Number(exponent)

    if (shift >= 0) {
      // Moving the point right: consume the scale first, then grow the mantissa.
      const consumed = Math.min(shift, s)
      s -= consumed
      m *= pow10(shift - consumed)
    } else {
      s -= shift
    }
  }

  if (sign === '-') {
    m = -m
  }

  return normalize(m, s)
}

/**
 * The wire's `number | string`, as a decimal.
 *
 * `ServerDecimal` is deliberately both (see CLAUDE.md): .NET's OpenAPI
 * describes every numeric as `number | string`, and a guard that accepted only
 * one of them would reject every value at runtime while compiling cleanly.
 *
 * A `number` is converted **through its decimal string**, not through its
 * binary value — `String(0.1)` is `"0.1"`, which is the price somebody typed,
 * whereas the underlying double is not exactly a tenth of anything.
 */
export function fromServerDecimal(value: number | string): Dec {
  if (typeof value === 'string') {
    return parse(value)
  }

  if (!Number.isFinite(value)) {
    throw new RangeError(`${String(value)} is not a decimal.`)
  }

  return parse(String(value))
}

/** An integer, exactly. */
export function fromInt(value: number | bigint): Dec {
  return { m: BigInt(value), s: 0 }
}

/**
 * The canonical decimal string, scale included.
 *
 * `1.5000` prints as `1.5000` rather than `1.5`, because the scale is part of
 * what is being asserted — the conformance corpus compares these strings, and
 * collapsing them would hide precisely the class of divergence it exists to
 * catch.
 */
export function toString(value: Dec): string {
  const negative = value.m < 0n
  const digits = abs(value.m)
    .toString()
    .padStart(value.s + 1, '0')

  const whole = digits.slice(0, digits.length - value.s)
  const fraction = value.s === 0 ? '' : `.${digits.slice(digits.length - value.s)}`

  return `${negative ? '-' : ''}${whole}${fraction}`
}

/**
 * A JS `number`, for display only.
 *
 * **Never feed this back into a calculation.** It is here so a total can reach
 * `Intl.NumberFormat`, and every use of it is one where the value has already
 * been rounded to the payable scale.
 */
export function toNumber(value: Dec): number {
  return Number(toString(value))
}

export { ONE }
