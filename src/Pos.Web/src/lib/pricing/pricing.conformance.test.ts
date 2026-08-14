/**
 * The TypeScript pricing engine, against the corpus the C# engine generated.
 *
 * **This is the load-bearing test of Phase 9.** CLAUDE.md invariant 3 says the
 * server computes every total and the client displays it, because two
 * implementations of tax and discount rules will differ eventually and the
 * place a difference surfaces is a customer disputing a receipt at a counter.
 * Offline there is no server to ask, so the choice is not between one engine
 * and two — it is between two engines and a till that cannot take money when
 * the line goes down.
 *
 * What makes two engines survivable is this file and its sibling,
 * `PricingConformanceTests`, reading **the same committed fixture**:
 *
 * - `tests/fixtures/pricing-conformance.json` is generated from
 *   `PricingCorpus` by the C# engine — 913 carts, both tax modes, the
 *   hand-worked cases first and 500 seeded random baskets per mode after.
 * - A change to either engine that is not a change to both turns one side red.
 *
 * **Amounts are compared as strings, scale included.** `12.97` and `12.9700`
 * are the same number and not the same result: a decimal's scale propagates
 * through the multiplication that follows it, and the pipeline rounds only at
 * the end, so a value carrying the wrong number of places moves a digit several
 * operations later. Comparing numerically would let exactly that through.
 *
 * ---
 *
 * **What this corpus does not cover, established by falsifying it.** Six
 * deliberate breaks were applied — three to each engine — and five turned this
 * file or its .NET sibling red: tax charged before the discounts, totals summed
 * from rounded lines, banker's rounding, the apportionment remainder onto the
 * last line instead of the largest, and an independently summed subtotal.
 *
 * The sixth did not: changing division to round **half away from zero** instead
 * of half to even leaves all 913 carts identical. That is not a gap in the
 * corpus so much as a fact about the arithmetic — an exact tie at the 28th
 * decimal place requires a quotient that terminates precisely there, and a
 * ratio of two four-decimal money amounts never does. The behaviour is real and
 * is pinned one layer down, in `decimal.test.ts`, against values read off the
 * .NET type; that break fails three of its cases.
 *
 * Worth stating rather than leaving implicit: this file proves the two engines
 * agree **on carts a shop can actually ring**, and the unit tests prove the
 * primitives agree everywhere else. Neither claim covers the other.
 */

import { describe, expect, it } from 'vitest'
/*
 * The fixture as a string, through Vite, rather than through `node:fs`.
 *
 * `tsconfig.app.json` deliberately keeps Node's globals out of `src`, on the
 * grounds that anything reaching for `process` or `node:fs` there would compile
 * and then fail in a browser. A test living in `src` is still `src`, so it gets
 * the file the way the app would: `?raw` is a Vite import, typed by
 * `vite/client`, and it works in the same module graph as everything else.
 *
 * `?raw` rather than importing the JSON directly, because a 1.7 MB literal
 * would have TypeScript infer a type from every one of its 913 cases — minutes
 * of `tsc` for a shape declared below in ten lines.
 */
import corpusJson from '../../../../../tests/fixtures/pricing-conformance.json?raw'
import { parse, toString } from './decimal'
import { money, type Money } from './money'
import { price, type Cart, type CartLine } from './pricingEngine'
import type { TaxMode } from './taxCalculator'

interface CorpusFile {
  About: string
  Seed: number
  Cases: CorpusCase[]
}

interface CorpusCase {
  Name: string
  Cart: {
    Lines: { Quantity: string; UnitPrice: string; TaxRate: string; LineDiscount: string }[]
    CartDiscount: string
    TaxMode: string
    CashRoundingIncrement: string
  }
  Expected: {
    Subtotal: string
    DiscountTotal: string
    TaxTotal: string
    RoundingAdjustment: string
    Total: string
    Lines: {
      LineNumber: number
      Gross: string
      Subtotal: string
      Discount: string
      CartDiscountShare: string
      Tax: string
      Total: string
    }[]
  }
}

const corpus = JSON.parse(corpusJson) as CorpusFile

function toCart(entry: CorpusCase): Cart {
  const lines: CartLine[] = entry.Cart.Lines.map((line, index) => ({
    productId: `corpus-${String(index + 1)}`,
    description: `Line ${String(index + 1)}`,
    quantity: parse(line.Quantity),
    unitPrice: money(parse(line.UnitPrice)),
    taxRate: parse(line.TaxRate),
    lineDiscount: money(parse(line.LineDiscount)),
  }))

  return {
    lines,
    cartDiscount: money(parse(entry.Cart.CartDiscount)),
    taxMode: entry.Cart.TaxMode as TaxMode,
    cashRoundingIncrement: parse(entry.Cart.CashRoundingIncrement),
  }
}

/** The failing case, in the same shape `PricingCorpus.Describe` prints it. */
function describeCart(entry: CorpusCase): string {
  const lines = entry.Cart.Lines.map(
    (l) => `${l.Quantity}×${l.UnitPrice}@${l.TaxRate}-${l.LineDiscount}`,
  ).join('; ')

  return (
    `${entry.Name}: mode=${entry.Cart.TaxMode}, discount=${entry.Cart.CartDiscount}, ` +
    `rounding=${entry.Cart.CashRoundingIncrement}, lines=[${lines}]`
  )
}

describe('the corpus itself', () => {
  it('is the file the C# engine generated, and has not shrunk', () => {
    // A corpus that quietly emptied would make every assertion below vacuous.
    // The same floor the .NET side asserts.
    expect(corpus.Cases.length).toBeGreaterThan(900)
    expect(corpus.Seed).toBe(20260802)
    expect(corpus.About).toContain('Do not hand-edit')
  })

  it('covers both tax modes and the hand-worked carts', () => {
    const names = corpus.Cases.map((c) => c.Name)

    expect(names).toContain('hand-checked-inclusive')
    expect(names).toContain('hand-checked-exclusive')
    expect(names).toContain('production-smoke-sale')

    expect(corpus.Cases.some((c) => c.Cart.TaxMode === 'Inclusive')).toBe(true)
    expect(corpus.Cases.some((c) => c.Cart.TaxMode === 'Exclusive')).toBe(true)
  })
})

describe('the port reproduces the C# engine', () => {
  it('agrees on the header totals of every cart', () => {
    /*
     * One test over every cart rather than one test per cart.
     *
     * 913 named cases would drown the reporter and turn a single divergence
     * into 913 lines of output. What matters on a failure is *which* cart and
     * *which* amount, and collecting them means a systematic break — a rounding
     * mode, an operation order — shows up as hundreds of failures with one
     * shape rather than as the first alphabetical one.
     */
    const failures: string[] = []

    for (const entry of corpus.Cases) {
      const actual = price(toCart(entry))

      const checks: [string, Money, string][] = [
        ['subtotal', actual.subtotal, entry.Expected.Subtotal],
        ['discountTotal', actual.discountTotal, entry.Expected.DiscountTotal],
        ['taxTotal', actual.taxTotal, entry.Expected.TaxTotal],
        ['roundingAdjustment', actual.roundingAdjustment, entry.Expected.RoundingAdjustment],
        ['total', actual.total, entry.Expected.Total],
      ]

      for (const [field, got, want] of checks) {
        if (toString(got) !== want) {
          failures.push(`${describeCart(entry)}\n    ${field}: got ${toString(got)}, want ${want}`)
        }
      }
    }

    expect(
      failures.slice(0, 20).join('\n  '),
      `${String(failures.length)} header divergences across ${String(corpus.Cases.length)} carts`,
    ).toBe('')
  })

  it('agrees on every priced line of every cart', () => {
    // The lines are where a divergence appears first: the header totals are
    // rounded to two places and can agree by luck where the four-place line
    // amounts underneath them do not.
    const failures: string[] = []

    for (const entry of corpus.Cases) {
      const actual = price(toCart(entry))

      if (actual.lines.length !== entry.Expected.Lines.length) {
        failures.push(`${describeCart(entry)}\n    line count: ${String(actual.lines.length)}`)
        continue
      }

      for (const [index, want] of entry.Expected.Lines.entries()) {
        const got = actual.lines[index]!

        const checks: [string, Money, string][] = [
          ['gross', got.gross, want.Gross],
          ['subtotal', got.subtotal, want.Subtotal],
          ['discount', got.discount, want.Discount],
          ['cartDiscountShare', got.cartDiscountShare, want.CartDiscountShare],
          ['tax', got.tax, want.Tax],
          ['total', got.total, want.Total],
        ]

        if (got.lineNumber !== want.LineNumber) {
          failures.push(`${describeCart(entry)}\n    line ${String(index)}: wrong lineNumber`)
        }

        for (const [field, value, expected] of checks) {
          if (toString(value) !== expected) {
            failures.push(
              `${describeCart(entry)}\n    line ${String(want.LineNumber)} ${field}: ` +
                `got ${toString(value)}, want ${expected}`,
            )
          }
        }
      }
    }

    expect(
      failures.slice(0, 20).join('\n  '),
      `${String(failures.length)} line divergences across ${String(corpus.Cases.length)} carts`,
    ).toBe('')
  })
})

describe('the properties the C# suite asserts, asserted here too', () => {
  it('apportions a cart discount to exactly the discount given', () => {
    // Exactly, not nearly. A basket that apportions to 4.99 of a 5.00 promise
    // is a cent the shop gave away and cannot account for.
    for (const entry of corpus.Cases) {
      if (entry.Cart.CartDiscount === '0') {
        continue
      }

      const actual = price(toCart(entry))

      let sum = parse('0')
      for (const line of actual.lines) {
        sum = money(parse(toString(sum)))
        sum = { m: sum.m, s: sum.s }
        sum = addDec(sum, line.cartDiscountShare)
      }

      expect(compareValue(sum, parse(entry.Cart.CartDiscount)), describeCart(entry)).toBe(0)
    }
  })

  it('satisfies invariant 1 on the stored two-decimal values', () => {
    // Total == Subtotal − DiscountTotal + TaxTotal + RoundingAdjustment.
    // The property that guards the derived Subtotal: if someone "simplifies" it
    // back to a fourth independently rounded sum, this is what notices.
    for (const entry of corpus.Cases) {
      const s = price(toCart(entry))

      const identity = addDec(
        addDec(subtractDec(s.subtotal, s.discountTotal), s.taxTotal),
        s.roundingAdjustment,
      )

      expect(compareValue(identity, s.total), describeCart(entry)).toBe(0)
    }
  })
})

// Local helpers, kept out of the money module: these operate on raw decimals
// for assertion purposes only and are not arithmetic the pipeline performs.
function addDec(a: { m: bigint; s: number }, b: { m: bigint; s: number }) {
  const scale = Math.max(a.s, b.s)
  return {
    m: a.m * 10n ** BigInt(scale - a.s) + b.m * 10n ** BigInt(scale - b.s),
    s: scale,
  }
}

function subtractDec(a: { m: bigint; s: number }, b: { m: bigint; s: number }) {
  const scale = Math.max(a.s, b.s)
  return {
    m: a.m * 10n ** BigInt(scale - a.s) - b.m * 10n ** BigInt(scale - b.s),
    s: scale,
  }
}

function compareValue(a: { m: bigint; s: number }, b: { m: bigint; s: number }): number {
  const scale = Math.max(a.s, b.s)
  const left = a.m * 10n ** BigInt(scale - a.s)
  const right = b.m * 10n ** BigInt(scale - b.s)

  return left < right ? -1 : left > right ? 1 : 0
}
