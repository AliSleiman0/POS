/**
 * The decimal port, against values read out of the real .NET type.
 *
 * **Every expectation here was produced by running C#**, not by reasoning about
 * what it ought to do — a probe printed the mantissa and scale of each result
 * and those are transcribed below. That matters most for the cases nobody would
 * guess: that division rounds half to *even* while the pipeline rounds half
 * away from zero, and that a quotient's scale depends on the operands' scales
 * rather than on the value.
 *
 * The conformance corpus (`pricing.conformance.test.ts`) is the real guarantee.
 * This file is the fast one, and it is where a failure is legible: a corpus
 * mismatch says "cart 214 disagrees", this says which operation.
 */

import { describe, expect, it } from 'vitest'
import {
  add,
  compare,
  divide,
  fromServerDecimal,
  MAX_MANTISSA,
  multiply,
  parse,
  round,
  subtract,
  toString,
} from './decimal'

/** Asserts a decimal's value **and its scale**, which is half of what is being ported. */
function expectDec(actual: { m: bigint; s: number }, text: string, scale: number) {
  expect(toString(actual)).toBe(text)
  expect(actual.s).toBe(scale)
}

describe('parsing', () => {
  it('keeps the scale the literal was written with', () => {
    // 1.5 and 1.5000 are the same number and not the same decimal. The scale
    // propagates through multiplication, so dropping it here would move a digit
    // several operations later.
    expectDec(parse('1.5'), '1.5', 1)
    expectDec(parse('1.5000'), '1.5000', 4)
    expectDec(parse('0.0000'), '0.0000', 4)
  })

  it('reads a sign, a bare integer and a leading point', () => {
    expectDec(parse('-2.75'), '-2.75', 2)
    expectDec(parse('42'), '42', 0)
    expectDec(parse('.5'), '.5'.replace('.', '0.'), 1)
  })

  it('reads exponent notation, because String(1e-7) produces it', () => {
    expectDec(parse('1e-7'), '0.0000001', 7)
    expectDec(parse('1.23e2'), '123', 0)
    expectDec(parse('1.23e1'), '12.3', 1)
  })

  it('refuses anything it does not fully recognise', () => {
    // Strict on purpose: a price that silently parsed as zero would have the
    // till sell the item for nothing and report success.
    for (const bad of ['', '  ', 'abc', '1.2.3', '1,2', '£1.20', '1.2x']) {
      expect(() => parse(bad), bad).toThrow(RangeError)
    }
  })

  it('takes a server decimal as either a number or a string', () => {
    // ServerDecimal is `number | string` deliberately — a guard that accepted
    // only one would reject every value at runtime while compiling cleanly.
    expectDec(fromServerDecimal('1.2000'), '1.2000', 4)
    expectDec(fromServerDecimal(1.2), '1.2', 1)
    expectDec(fromServerDecimal(0.1), '0.1', 1)
  })
})

describe('addition and subtraction', () => {
  it('aligns to the wider scale, exactly', () => {
    expectDec(add(parse('0.1'), parse('0.02')), '0.12', 2)
    expectDec(add(parse('1.0000'), parse('2')), '3.0000', 4)
    expectDec(subtract(parse('1.20'), parse('0.9756')), '0.2244', 4)
  })

  it('is the arithmetic a JS number gets wrong', () => {
    // 0.1 + 0.2 !== 0.3 is the whole reason this file exists.
    expect(toString(add(parse('0.1'), parse('0.2')))).toBe('0.3')
    expect(0.1 + 0.2).not.toBe(0.3)
  })

  it('drops the smaller operand when alignment would overflow the mantissa', () => {
    // Read off .NET: 79228162514264337593543950335m + 0.4m is unchanged, because
    // aligning to one place needs a mantissa that does not fit in 96 bits.
    expectDec(
      add(parse('79228162514264337593543950335'), parse('0.4')),
      '79228162514264337593543950335',
      0,
    )

    // And the case where it loses exactly one digit, rounding half to even.
    expectDec(
      add(parse('7922816251426433759354395033.4'), parse('0.05')),
      '7922816251426433759354395033.4',
      1,
    )
  })
})

describe('multiplication', () => {
  it('sums the scales and stays exact', () => {
    expectDec(multiply(parse('0.3333'), parse('1.2340')), '0.41129220', 8)
    expectDec(multiply(parse('1.2345'), parse('3.7')), '4.56765', 5)
  })

  it('rounds half to even when the scales sum past the limit', () => {
    // The three cases that distinguish to-even from away-from-zero, all read
    // off .NET: 1.5 -> 2, 2.5 -> 2, 3.5 -> 4.
    expectDec(
      multiply(parse('0.0000000000000000000000000003'), parse('0.5')),
      '0.0000000000000000000000000002',
      28,
    )
    expectDec(
      multiply(parse('0.0000000000000000000000000005'), parse('0.5')),
      '0.0000000000000000000000000002',
      28,
    )
    expectDec(
      multiply(parse('0.0000000000000000000000000007'), parse('0.5')),
      '0.0000000000000000000000000004',
      28,
    )
  })
})

describe('division', () => {
  it('gives the exact quotient when it terminates', () => {
    expectDec(divide(parse('1'), parse('8')), '0.125', 3)
    expectDec(divide(parse('10'), parse('4')), '2.5', 1)
  })

  it('never returns fewer places than the dividend carried', () => {
    // The preferred scale: dividend's minus divisor's, floored at zero. Read off
    // .NET, and not what an implementation would do by accident.
    expectDec(divide(parse('1.0000'), parse('1')), '1.0000', 4)
    expectDec(divide(parse('2.5000'), parse('5')), '0.5000', 4)
    expectDec(divide(parse('1'), parse('0.5')), '2', 0)
  })

  it('extends the scale when the exact quotient needs more places', () => {
    expectDec(divide(parse('3.3333'), parse('10.0000')), '0.33333', 5)
    expectDec(divide(parse('99999.9999'), parse('100000.0000')), '0.999999999', 9)
  })

  it('fills the mantissa when the quotient does not terminate', () => {
    expectDec(divide(parse('1'), parse('3')), '0.3333333333333333333333333333', 28)
    expectDec(divide(parse('2'), parse('3')), '0.6666666666666666666666666667', 28)
    expectDec(divide(parse('1'), parse('7')), '0.1428571428571428571428571429', 28)
    expectDec(divide(parse('5'), parse('9')), '0.5555555555555555555555555556', 28)
    expectDec(divide(parse('4'), parse('9')), '0.4444444444444444444444444444', 28)
  })

  it('gives up a place when 28 would not fit in the mantissa', () => {
    // 10/3 fits 29 mantissa digits at scale 28; 100/3 would need 30 and cannot,
    // so .NET drops to 27 places. A fixed "28 significant digits" rule gets this
    // wrong, which is why the limit here is the 96-bit mantissa.
    expectDec(divide(parse('10'), parse('3')), '3.3333333333333333333333333333', 28)
    expectDec(divide(parse('100'), parse('3')), '33.333333333333333333333333333', 27)
  })

  it('rounds half to even at the cut', () => {
    expectDec(
      divide(parse('0.0000000000000000000000000003'), parse('2')),
      '0.0000000000000000000000000002',
      28,
    )
    expectDec(
      divide(parse('0.0000000000000000000000000005'), parse('2')),
      '0.0000000000000000000000000002',
      28,
    )
    expectDec(
      divide(parse('0.0000000000000000000000000007'), parse('2')),
      '0.0000000000000000000000000004',
      28,
    )
  })

  it('refuses to divide by zero rather than producing an infinity', () => {
    expect(() => divide(parse('1'), parse('0'))).toThrow(RangeError)
  })

  it('handles the inclusive-tax back-out the pipeline actually performs', () => {
    // €1.20 at 23% inclusive — the exact sale Phase 8 rang against production.
    expectDec(
      divide(parse('1.20'), add(parse('1'), parse('0.23'))),
      '0.9756097560975609756097560976',
      28,
    )
  })
})

describe('rounding', () => {
  it('rounds half away from zero, not to even', () => {
    // The pipeline's own rule, and the opposite of what division does. .NET's
    // default is banker's rounding and would give 0.12 and 2.67 here.
    expect(toString(round(parse('0.125'), 2))).toBe('0.13')
    expect(toString(round(parse('2.675'), 2))).toBe('2.68')
    expect(toString(round(parse('1.005'), 2))).toBe('1.01')
  })

  it('goes away from zero in both directions', () => {
    expect(toString(round(parse('-0.125'), 2))).toBe('-0.13')
    expect(toString(round(parse('-2.5'), 0))).toBe('-3')
    expect(toString(round(parse('2.5'), 0))).toBe('3')
  })

  it('leaves a value already at or below the scale alone rather than padding', () => {
    // decimal.Round(2.5m, 4) is 2.5, not 2.5000.
    expectDec(round(parse('2.5'), 4), '2.5', 1)
    expectDec(round(parse('0.00005'), 4), '0.0001', 4)
    expectDec(round(parse('0.00005'), 2), '0.00', 2)
  })
})

describe('comparison', () => {
  it('compares by value, across scales', () => {
    expect(compare(parse('1.5'), parse('1.5000'))).toBe(0)
    expect(compare(parse('1.5'), parse('1.50001'))).toBe(-1)
    expect(compare(parse('-1'), parse('0.0000'))).toBe(-1)
  })
})

describe('the limits themselves', () => {
  it('bounds the mantissa where .NET bounds it', () => {
    // decimal.MaxValue read off the real type. Reproducing the limit is what
    // makes the two implementations agree when precision runs out.
    expect(MAX_MANTISSA).toBe(79228162514264337593543950335n)
  })
})
