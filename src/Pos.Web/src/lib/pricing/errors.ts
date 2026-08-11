/**
 * The pricing engine's own failures.
 *
 * Mirrors `Pos.Core.Exceptions.InvalidDiscountException`, and mirrors its
 * status too: the server answers a discount larger than the line it applies to
 * with `422 .../invalid-discount`. Offline there is no server to say so, and
 * the till has to reach the same refusal on its own — otherwise a basket the
 * server would reject is one a cashier can complete offline and only discover
 * as a permanent sync failure, hours later, with the money already taken.
 */

/** A discount exceeds what it applies to, or is negative. */
export class InvalidDiscountError extends Error {
  constructor(message: string) {
    super(message)
    this.name = 'InvalidDiscountError'
  }
}
