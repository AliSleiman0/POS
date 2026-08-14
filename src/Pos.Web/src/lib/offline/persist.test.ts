import { describe, expect, it } from 'vitest'
import { describeRisk, LARGE_QUEUE, LONG_OFFLINE_MS } from './persist'

/**
 * When the app warns a shop that it is carrying risk.
 *
 * Two failure directions, and the quiet one is worse. A warning shown
 * constantly is one nobody reads, which costs the thresholds their meaning; a
 * warning never shown lets a shop trade all day into an evictable cache
 * believing it is safe. So the thresholds are asserted on both sides.
 */

const HOUR = 3_600_000

describe('describeRisk', () => {
  it('says nothing when there is nothing queued', () => {
    // No pending sales means no exposure, whatever the browser has promised.
    expect(
      describeRisk({ pendingSales: 0, oldestPendingAgeMs: null, persistence: 'denied' }),
    ).toBeNull()
  })

  it('says nothing about a small, fresh queue on protected storage', () => {
    // The ordinary case: a brief outage, a couple of sales. Warning here would
    // train staff to ignore the warning that matters.
    expect(
      describeRisk({ pendingSales: 2, oldestPendingAgeMs: 5 * 60_000, persistence: 'granted' }),
    ).toBeNull()
  })

  it('warns immediately when the browser has refused to protect the data', () => {
    /*
     * Ahead of every other threshold, and deliberately: one sale in storage the
     * browser may reclaim without warning is a different kind of exposure from
     * twenty in storage it has promised to keep.
     */
    const message = describeRisk({
      pendingSales: 1,
      oldestPendingAgeMs: 60_000,
      persistence: 'denied',
    })

    expect(message).not.toBeNull()
    expect(message).toMatch(/cleared without warning/i)
  })

  it('warns once the till has been offline for hours', () => {
    const message = describeRisk({
      pendingSales: 3,
      oldestPendingAgeMs: LONG_OFFLINE_MS + HOUR,
      persistence: 'granted',
    })

    expect(message).not.toBeNull()
    expect(message).toMatch(/best effort/i)
  })

  it('does not warn just below the offline threshold', () => {
    expect(
      describeRisk({
        pendingSales: 3,
        oldestPendingAgeMs: LONG_OFFLINE_MS - 60_000,
        persistence: 'granted',
      }),
    ).toBeNull()
  })

  it('warns once the queue is large, however recent it is', () => {
    // A busy shop can accumulate this in twenty minutes, and the amount of
    // money involved is what matters rather than how long it has been there.
    const message = describeRisk({
      pendingSales: LARGE_QUEUE,
      oldestPendingAgeMs: 60_000,
      persistence: 'granted',
    })

    expect(message).not.toBeNull()
    expect(message).toMatch(/browser cache/i)
  })

  it('does not warn just below the queue threshold', () => {
    expect(
      describeRisk({
        pendingSales: LARGE_QUEUE - 1,
        oldestPendingAgeMs: 60_000,
        persistence: 'granted',
      }),
    ).toBeNull()
  })

  it('never promises anything', () => {
    /*
     * The property the phase doc cares about most, asserted over every warning
     * this function can produce: none of them may read as reassurance. A shop
     * told its queued sales are "safe" and then losing them is the outcome that
     * ends the relationship.
     */
    const cases = [
      { pendingSales: 1, oldestPendingAgeMs: 60_000, persistence: 'denied' as const },
      {
        pendingSales: 3,
        oldestPendingAgeMs: LONG_OFFLINE_MS + HOUR,
        persistence: 'granted' as const,
      },
      { pendingSales: LARGE_QUEUE, oldestPendingAgeMs: 60_000, persistence: 'granted' as const },
    ]

    for (const input of cases) {
      const message = describeRisk(input)

      expect(message).not.toBeNull()
      expect(message!.toLowerCase()).not.toMatch(/\b(safe|secure|guaranteed|don't worry)\b/)
    }
  })
})
