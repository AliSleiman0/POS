/**
 * An idempotency key that belongs to an operation, not to an attempt.
 *
 * **CLAUDE.md invariant 6, and the reason it needs a hook at all.** A key minted
 * inside a mutation is new on every call, which makes the header decorative: the
 * timeout-then-retry that the header exists to survive charges the table twice.
 * The key has to be minted when the operation *begins* — when the waiter opens
 * the bill, not when they press Pay — and held unchanged across every attempt.
 *
 * **A key is bound to one request body.** `IdempotencyFilter` fingerprints the
 * raw body, so re-sending a changed body under the same key is
 * `409 idempotency-key-reused` and not a replay. When the content legitimately
 * changes — the guest adds a round before paying — that is new work and needs
 * `renew()`. Reaching the 409 means an earlier attempt **landed**, so the caller
 * must find out what it wrote rather than sending anything else.
 *
 * Nothing here is persisted. It is React state, like the manager's grant in
 * `OverrideProvider` — invariant 11's rule about what may reach storage is about
 * credentials, and this is not one, but an order lives on the server and a
 * reloaded till re-reads it rather than resuming a key it kept.
 */

import { useCallback, useState } from 'react'

export interface OperationKey {
  /** The key to send. Stable until `renew` is called. */
  key: string
  /** Start a new operation. Call when the request body legitimately changes. */
  renew: () => void
}

/**
 * Mints a key once and holds it.
 *
 * `useState` with a lazy initialiser rather than `useMemo`: `useMemo` is a
 * performance hint that React is free to discard and recompute, which would
 * silently hand out a second key mid-operation — the precise failure this hook
 * exists to prevent.
 */
export function useOperationKey(): OperationKey {
  const [key, setKey] = useState(() => crypto.randomUUID())

  const renew = useCallback(() => {
    setKey(crypto.randomUUID())
  }, [])

  return { key, renew }
}
