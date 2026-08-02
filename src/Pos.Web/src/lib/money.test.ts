import { describe, expect, it } from 'vitest'
import {
  formatMinorUnits,
  formatMoney,
  formatQuantity,
  formatRate,
  parseServerDecimal,
  provisionalLineTotal,
  provisionalSubtotal,
} from './money'

describe('formatMoney', () => {
  it('formats a server-sent decimal, which arrives as a JSON number', () => {
    expect(formatMoney(19, 'GBP', 'en-GB')).toBe('£19.00')
  })

  it('keeps trailing zeros that matter on a receipt', () => {
    expect(formatMoney(19.5, 'GBP', 'en-GB')).toBe('£19.50')
    expect(formatMoney(19.05, 'GBP', 'en-GB')).toBe('£19.05')
  })

  it('renders the hand-checked cart totals from Phase 3', () => {
    // HandCheckedCartTests: total 12.97, tax 2.02, discount 4.22, subtotal 15.17.
    expect(formatMoney(12.97, 'GBP', 'en-GB')).toBe('£12.97')
    expect(formatMoney(2.02, 'GBP', 'en-GB')).toBe('£2.02')
    expect(formatMoney(15.17, 'GBP', 'en-GB')).toBe('£15.17')
  })

  it('formats a negative amount, which is what a refund line is', () => {
    expect(formatMoney(-2.95, 'GBP', 'en-GB')).toBe('-£2.95')
  })

  it('accepts the string half of the declared decimal union', () => {
    // schema.d.ts types every amount `number | string`, because .NET's OpenAPI
    // describes a decimal as type: ["number", "string"]. Both must render.
    expect(formatMoney('12.97', 'GBP', 'en-GB')).toBe('£12.97')
    expect(formatMoney('-2.95', 'GBP', 'en-GB')).toBe('-£2.95')
  })

  it('rejects NaN rather than rendering it at a till', () => {
    // What a missing field turns into once it has been through arithmetic.
    expect(() => formatMoney(Number.NaN, 'GBP', 'en-GB')).toThrow(TypeError)
    expect(() => formatMoney(Number.POSITIVE_INFINITY, 'GBP', 'en-GB')).toThrow(TypeError)
  })
})

describe('parseServerDecimal', () => {
  it('parses the string form exactly', () => {
    expect(parseServerDecimal('0')).toBe(0)
    expect(parseServerDecimal('12.9700')).toBe(12.97)
    expect(parseServerDecimal('-3.5')).toBe(-3.5)
  })

  it('rejects what Number() would happily accept', () => {
    // Number('') is 0 and Number(' 1 ') is 1 — either would put a wrong figure
    // on a receipt rather than raising.
    expect(() => parseServerDecimal('')).toThrow(TypeError)
    expect(() => parseServerDecimal(' 1 ')).toThrow(TypeError)
    expect(() => parseServerDecimal('0x10')).toThrow(TypeError)
    expect(() => parseServerDecimal('1e3')).toThrow(TypeError)
    expect(() => parseServerDecimal('.5')).toThrow(TypeError)
  })
})

describe('formatQuantity and formatRate', () => {
  it('shows a whole quantity without padding decimals', () => {
    expect(formatQuantity(2, 'en-GB')).toBe('2')
    expect(formatQuantity('2', 'en-GB')).toBe('2')
  })

  it('shows a weighed quantity', () => {
    expect(formatQuantity(0.35, 'en-GB')).toBe('0.35')
  })

  it('renders a stored rate as the percentage a human reads', () => {
    expect(formatRate(0.2, 'en-GB')).toBe('20%')
    expect(formatRate('0.05', 'en-GB')).toBe('5%')
    expect(formatRate(0, 'en-GB')).toBe('0%')
  })
})

describe('provisional minor-unit arithmetic', () => {
  it('avoids the float error that makes 0.1 + 0.2 !== 0.3', () => {
    // The whole reason provisional totals use integers. In floats:
    expect(0.1 + 0.2).not.toBe(0.3)
    // In minor units:
    expect(provisionalSubtotal([10, 20])).toBe(30)
  })

  it('sums line totals exactly', () => {
    const lines = [1999, 550, 125]
    expect(provisionalSubtotal(lines)).toBe(2674)
    expect(formatMinorUnits(provisionalSubtotal(lines), 'GBP', 'en-GB')).toBe('£26.74')
  })

  it('handles decimal quantities for weighed goods', () => {
    // 0.350 kg at £12.99/kg
    expect(provisionalLineTotal(1299, 0.35)).toBe(455)
  })

  it('returns zero for an empty cart rather than NaN', () => {
    expect(provisionalSubtotal([])).toBe(0)
  })

  it('rejects a non-integer unit price, which would mean a float slipped in', () => {
    expect(() => provisionalLineTotal(12.99, 1)).toThrow(TypeError)
    expect(() => formatMinorUnits(12.5, 'GBP', 'en-GB')).toThrow(TypeError)
  })
})
