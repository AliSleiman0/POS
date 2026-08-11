import { describe, expect, it } from 'vitest'
import { describeMirror } from './mirrorAge'

/**
 * The one direction this must not fail in is the reassuring one.
 *
 * A mirror that is actually a day old and reads as fresh lets a till sell last
 * week's prices with complete confidence — and nobody finds out until a
 * customer argues about a shelf edge. So the boundaries are asserted on the
 * older side, not sampled in the middle.
 */

const NOW = Date.parse('2026-08-11T12:00:00.000Z')

function at(minutesAgo: number): number {
  return NOW - minutesAgo * 60_000
}

describe('describeMirror', () => {
  it('says there are no local prices when offline with no mirror', () => {
    // The worst state a till can be in: no server and nothing stored. It cannot
    // sell, and the cashier needs to know before a customer is standing there.
    expect(describeMirror(null, true, NOW)).toBe('No local prices')
  })

  it('is softer about an empty mirror while online, because it will fix itself', () => {
    expect(describeMirror(null, false, NOW)).toBe('Prices not yet stored')
  })

  it('reads as up to date within the first minute', () => {
    expect(describeMirror(at(0), false, NOW)).toBe('Prices up to date')
    expect(describeMirror(at(0.9), false, NOW)).toBe('Prices up to date')
  })

  it('counts minutes up to an hour', () => {
    expect(describeMirror(at(1), false, NOW)).toBe('Prices as of 1 min ago')
    expect(describeMirror(at(59), false, NOW)).toBe('Prices as of 59 min ago')
  })

  it('switches to hours at the hour, rather than saying 90 min', () => {
    expect(describeMirror(at(60), false, NOW)).toBe('Prices 1h old')
    expect(describeMirror(at(23 * 60 + 59), false, NOW)).toBe('Prices 23h old')
  })

  it('switches to days at a day, and says so bluntly', () => {
    // "1440 min ago" is technically true and tells nobody anything.
    expect(describeMirror(at(24 * 60), false, NOW)).toBe('Prices 1 days old')
    expect(describeMirror(at(9 * 24 * 60), false, NOW)).toBe('Prices 9 days old')
  })

  it('never rounds an old mirror down into a fresher bucket', () => {
    /*
     * The property that matters, stated over the boundaries rather than trusted
     * to the arithmetic: every threshold floors, so a mirror is described as at
     * least as old as it is and never as newer.
     */
    for (const minutes of [1, 59, 60, 61, 1439, 1440, 1441]) {
      const text = describeMirror(at(minutes), false, NOW)

      expect(text, `${String(minutes)} min`).not.toBe('Prices up to date')
    }
  })
})
