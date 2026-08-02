/**
 * Idempotency keys for the money- and stock-moving endpoints.
 *
 * CLAUDE.md invariant 6: sales, voids, refunds, stock adjustments and shift
 * open/close all require a client-generated GUID in an `Idempotency-Key`
 * header. A replay returns the original status and body byte-for-byte, with
 * `Idempotent-Replay: true`.
 */

/** The header the API reads. See `src/Pos.Api/Idempotency/IdempotencyFilter.cs`. */
export const IDEMPOTENCY_HEADER = 'Idempotency-Key'

/** Set by the API on a replayed response. */
export const REPLAY_HEADER = 'Idempotent-Replay'

/**
 * A fresh key.
 *
 * **Generate this before the first attempt and reuse the same value on every
 * retry.** A key minted per attempt makes the header decorative: the second
 * request carries a key the server has never seen, so it is new work, and the
 * customer is charged twice. Hold it next to the operation it identifies — for
 * a sale that means creating it when the cart is first tendered, not inside the
 * fetch call.
 *
 * A disabled submit button is not this mechanism and never was. It does not
 * survive a reload, a flaky connection, or a second till.
 */
export function newIdempotencyKey(): string {
  return crypto.randomUUID()
}

/**
 * Whether a response was a replay rather than fresh work.
 *
 * Worth surfacing: "this sale was already recorded" is a different thing to
 * tell a cashier than "sale recorded".
 */
export function wasReplayed(response: Response): boolean {
  return response.headers.get(REPLAY_HEADER) === 'true'
}
