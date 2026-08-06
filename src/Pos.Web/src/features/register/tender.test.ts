import { describe, expect, it } from 'vitest'
import {
  isCovered,
  provisionalChangeMinor,
  quickCash,
  remainingMinor,
  tenderedMinor,
  type Tender,
} from './tender'

/**
 * The cash arithmetic.
 *
 * All of it is integer minor units, which is the point: this is the one part of
 * the till that adds money up on the client, and it does so only for figures
 * that are *suggestions* or *provisional*. What the customer is owed comes back
 * from the server on the sale.
 */
describe('quickCash', () => {
  function notes(totalMinor: number): number[] {
    return quickCash(totalMinor, 'EUR')
  }

  it('offers exact first, because it is the commonest press', () => {
    expect(notes(1415)[0]).toBe(1415)
  })

  it('offers the next whole unit for an "and a bit" total', () => {
    // €4.60 paid with a fiver, which is most of a shop's transactions.
    expect(notes(460)).toEqual([460, 500, 1000, 2000])
  })

  it('does not offer a note smaller than the total', () => {
    // Nothing here may be less than €14.15 except nothing at all — a button that
    // under-tenders is a button that produces a 409 at the counter.
    for (const amount of notes(1415)) {
      expect(amount).toBeGreaterThanOrEqual(1415)
    }
  })

  it('does not offer the same amount twice', () => {
    // On exactly €5.00 the next whole unit *is* the five-euro note. Two
    // identical buttons side by side is a misread waiting to happen.
    const suggestions = notes(500)

    expect(suggestions).toEqual([...new Set(suggestions)])
    expect(suggestions[0]).toBe(500)
    expect(suggestions[1]).toBe(1000)
  })

  it('runs out gracefully on a total above every note', () => {
    // A €62 basket: exact and the next whole unit, and nothing else to suggest.
    expect(notes(6200)).toEqual([6200])
  })

  it('offers nothing for an empty cart', () => {
    expect(notes(0)).toEqual([])
  })

  it('falls back to a sensible ladder for an unknown currency', () => {
    expect(quickCash(460, 'XYZ')).toEqual([460, 500, 1000, 2000])
  })
})

describe('the running balance', () => {
  function tender(...amounts: number[]): Tender[] {
    return amounts.map((amountMinor, index) => ({ key: `t${String(index)}`, amountMinor }))
  }

  it('adds a split tender up exactly', () => {
    // The float error this avoids: 10.00 + 4.15 + 0.25 in IEEE-754.
    expect(tenderedMinor(tender(1000, 415, 25))).toBe(1440)
  })

  it('counts down what is still owed', () => {
    expect(remainingMinor(1415, tender(1000))).toBe(415)
    expect(remainingMinor(1415, tender(1000, 415))).toBe(0)
  })

  it('never shows a negative balance, because an overshoot is change', () => {
    // "Still owed −€5.85" invites a cashier to ask for more money.
    expect(remainingMinor(1415, tender(2000))).toBe(0)
    expect(provisionalChangeMinor(1415, tender(2000))).toBe(585)
  })

  it('has no change to give until the total is covered', () => {
    expect(provisionalChangeMinor(1415, tender(1000))).toBe(0)
  })

  it('will not complete an empty or short tender', () => {
    expect(isCovered(1415, [])).toBe(false)
    expect(isCovered(1415, tender(1000))).toBe(false)
    expect(isCovered(1415, tender(1415))).toBe(true)
    expect(isCovered(1415, tender(1000, 500))).toBe(true)
  })

  it('treats a free cart as needing a tender anyway', () => {
    // A 100% discount still ends in someone pressing Complete. Zero tenders on a
    // zero total would otherwise submit an empty `tenders` array, which the
    // server refuses with "at least one tender is required".
    expect(isCovered(0, [])).toBe(false)
    expect(isCovered(0, tender(0))).toBe(true)
  })
})
