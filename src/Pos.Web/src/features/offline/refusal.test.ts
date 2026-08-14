import { describe, expect, it } from 'vitest'
import { ErrorType } from '@/api/problem'
import { describeRefusal } from './refusal'

/**
 * What the review screen tells a manager.
 *
 * The wording is the feature here. A queue that says "409" hands somebody a
 * puzzle at the end of a shift; what they need is whether money is missing and
 * what to do next. So these assert the *shape of the answer* — severity and
 * remedy — rather than exact prose, which should be free to improve.
 *
 * The one thing asserted about the prose is the thing that must never drift:
 * that an unrecognised refusal admits it is unrecognised.
 */

describe('describeRefusal', () => {
  it('offers to re-file a sale whose drawer was closed', () => {
    // The common case, and the one 9.4 exists to make resolvable: the till was
    // offline while a manager closed the shift from the back office.
    const refusal = describeRefusal(ErrorType.shiftClosed, null)

    expect(refusal.remedy).toBe('refile')
    expect(refusal.severity).toBe('warning')
  })

  it('refuses to offer a retry when the key already bought something', () => {
    /*
     * The dangerous one. An earlier attempt landed with a different body, so the
     * customer may already have been charged — and an action button reading
     * "send again" is the one thing this screen must not show.
     */
    const refusal = describeRefusal(ErrorType.idempotencyKeyReused, null)

    expect(refusal.remedy).toBe('investigate')
    expect(refusal.severity).toBe('danger')
    expect(refusal.consequence).toMatch(/already have been charged/i)
  })

  it('treats a rejected timestamp as needing a person, not a button', () => {
    // Either the till has been queuing for days or its clock is wrong. Nothing
    // this screen can do makes that certain again.
    const refusal = describeRefusal(ErrorType.offlineSaleTimestampInvalid, null)

    expect(refusal.remedy).toBe('manual')
    expect(refusal.severity).toBe('danger')
  })

  it('explains an unauthorised discount without offering to force it through', () => {
    const refusal = describeRefusal(ErrorType.overrideRequired, null)

    expect(refusal.remedy).toBe('manual')
  })

  it('admits when it does not recognise the refusal', () => {
    /*
     * The important one, and the temptation it resists.
     *
     * A friendly catch-all — "something went wrong, try again" — would be the
     * worst thing on this screen: a refused sale is money the server does not
     * have, and telling a manager it is probably fine is worse than telling
     * them nothing at all.
     */
    const refusal = describeRefusal('some-error-nobody-has-seen', 'The server said no.')

    expect(refusal.severity).toBe('danger')
    expect(refusal.remedy).toBe('manual')
    expect(refusal.summary).toMatch(/does not recognise/i)

    // And it passes the server's own words through, since they are the only
    // clue anybody has.
    expect(refusal.consequence).toContain('The server said no.')
  })

  it('still says the money was taken when there is no message at all', () => {
    const refusal = describeRefusal(null, null)

    expect(refusal.consequence).toMatch(/money was taken/i)
  })

  it('never describes a refusal as harmless', () => {
    // Every one of these is a sale the server does not have. None of them
    // should read as routine, whatever else it says.
    const types = [
      ErrorType.shiftClosed,
      ErrorType.idempotencyKeyReused,
      ErrorType.offlineSaleTimestampInvalid,
      ErrorType.overrideRequired,
      ErrorType.underTender,
      null,
    ]

    for (const type of types) {
      const refusal = describeRefusal(type, null)

      expect(['warning', 'danger'], String(type)).toContain(refusal.severity)
      expect(refusal.consequence.length, String(type)).toBeGreaterThan(40)
    }
  })
})
