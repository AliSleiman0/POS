/**
 * What a refused sale means, and what a person can do about it.
 *
 * **The review queue is only worth having if it explains itself.** A screen
 * listing "sale failed: 409" hands a manager a puzzle at the end of a shift,
 * when what they need is the sentence that tells them whether money is missing.
 * So every refusal the server can give this queue is named here, with the
 * consequence spelled out and the remedy stated — and anything unrecognised
 * falls through to a wording that admits it does not know rather than inventing
 * a reassurance.
 *
 * Kept out of the component so it can be tested as data.
 */

import { ErrorType } from '@/api/problem'

/** What a person may do with a refused sale. */
export type Remedy =
  /** File it against the drawer that is open now. The common one. */
  | 'refile'
  /** Check what the server already has before touching anything. */
  | 'investigate'
  /** Nothing this screen can do; it needs the shop's own records. */
  | 'manual'

export interface Refusal {
  /** One line, in the words a shop would use. */
  summary: string
  /** What it means for the money. The part a manager is actually asking. */
  consequence: string
  remedy: Remedy
  /** Whether the money is known to be safe. Drives how loud the row is. */
  severity: 'warning' | 'danger'
}

export function describeRefusal(type: string | null, message: string | null): Refusal {
  switch (type) {
    case ErrorType.shiftClosed:
      return {
        summary: 'The drawer was closed before this sale reached the server.',
        consequence:
          'The cash is in the till and the shift it was counted against does not include it, so that shift will read as over by this amount.',
        remedy: 'refile',
        severity: 'warning',
      }

    case ErrorType.idempotencyKeyReused:
      return {
        summary: 'This sale was already sent, carrying different contents.',
        consequence:
          'An earlier attempt with this reference landed. The customer may already have been charged — do not send it again until somebody has looked the sale up.',
        remedy: 'investigate',
        severity: 'danger',
      }

    case ErrorType.offlineSaleTimestampInvalid:
      return {
        summary: 'The server would not accept the time this sale is dated.',
        consequence:
          'Either this till has been queuing for days, or its clock is wrong. The sale is real and the money was taken; what nobody can be sure of is when.',
        remedy: 'manual',
        severity: 'danger',
      }

    case ErrorType.overrideRequired:
      return {
        summary: 'A discount or price override on this sale was never authorised.',
        consequence:
          'The customer paid the discounted amount. The server will not record it without a manager approving the same sale again.',
        remedy: 'manual',
        severity: 'warning',
      }

    case ErrorType.underTender:
      return {
        summary: 'The recorded payment does not cover the sale.',
        consequence:
          'The till should not have accepted this. Treat the amounts as unreliable and reconcile against the drawer.',
        remedy: 'manual',
        severity: 'danger',
      }

    default:
      /*
       * Unrecognised, and it says so.
       *
       * The temptation is a friendly catch-all — "something went wrong, try
       * again". That would be the one thing this screen must not do: a refused
       * sale is money the server does not have, and telling a manager it is
       * probably fine is worse than telling them nothing.
       */
      return {
        summary: 'The server refused this sale and the till does not recognise the reason.',
        consequence:
          message === null
            ? 'The money was taken and the sale is not recorded. It needs checking against the drawer by hand.'
            : `The money was taken and the sale is not recorded. The server said: ${message}`,
        remedy: 'manual',
        severity: 'danger',
      }
  }
}
