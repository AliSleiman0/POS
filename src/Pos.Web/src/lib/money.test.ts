import { describe, expect, it } from 'vitest'
import { formatMinorUnits, formatMoney, provisionalLineTotal, provisionalSubtotal } from './money'

describe('formatMoney', () => {
  it('formats a server-sent decimal string', () => {
    expect(formatMoney('19.00', 'GBP', 'en-GB')).toBe('£19.00')
  })

  it('keeps trailing zeros that matter on a receipt', () => {
    expect(formatMoney('19.50', 'GBP', 'en-GB')).toBe('£19.50')
    expect(formatMoney('19.05', 'GBP', 'en-GB')).toBe('£19.05')
  })

  it('rejects a non-numeric amount rather than rendering NaN at a till', () => {
    expect(() => formatMoney('not-money', 'GBP', 'en-GB')).toThrow(TypeError)
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
