import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import type { Policy } from '@/auth/policies'
import { isEmpty } from './cart'
import { useCart } from './cartContext'
import { ManagerAuthorizationDialog } from './ManagerAuthorizationDialog'
import {
  OverrideContext,
  type OverrideAuthorization,
  type OverrideContextValue,
} from './overrideContext'

/** A request waiting on the manager, and the caller waiting on the request. */
interface Pending {
  policies: readonly Policy[]
  settle: (authorization: OverrideAuthorization | null) => void
}

/**
 * Holds a manager's authorisation for the life of a cart.
 *
 * **Mounted beside the cart, not inside it, and that is the point.** 5.5 adds
 * `sessionStorage` persistence to `CartProvider` so a reload mid-submit
 * recovers; a grant living in the cart reducer would be written to disk by that
 * change, silently, as a side effect of a feature about something else. Here it
 * is React state and nothing else, so a reload loses it — which is the correct
 * outcome for a five-minute credential.
 *
 * `authorize` is promise-shaped so a caller reads as one thing: ask for
 * permission, and if it does not come back, change nothing.
 */
export function OverrideProvider({ children }: { children: ReactNode }) {
  const { cart } = useCart()

  const [authorization, setAuthorization] = useState<OverrideAuthorization | null>(null)
  const [pending, setPending] = useState<Pending | null>(null)

  /*
   * An authorisation belongs to the sale it was given for.
   *
   * An empty cart is the next customer, and a grant that survived into theirs
   * would let a discount the manager approved for somebody else be applied to
   * whatever is scanned next. Covers voiding the cart and voiding the last line
   * alike, and in 5.4 it will cover a completed sale for free.
   */
  const emptied = isEmpty(cart)

  useEffect(() => {
    if (emptied) {
      setAuthorization(null)
    }
  }, [emptied])

  // Read inside `authorize` without making the callback change on every grant,
  // which would re-render every consumer of the context each time.
  const held = useRef<OverrideAuthorization | null>(null)
  held.current = authorization

  const authorize = useCallback(
    (policies: readonly Policy[]): Promise<OverrideAuthorization | null> => {
      const current = held.current

      if (current !== null && policies.every((policy) => current.policies.includes(policy))) {
        return Promise.resolve(current)
      }

      /*
       * The union, not just what was asked for.
       *
       * A cart can end up carrying a discount and a price override, and the sale
       * presents one header. Asking only for the new policy would replace a
       * grant that already covered the old one, and the sale would then be
       * refused for something the manager had already approved.
       */
      const union = [...new Set([...(current?.policies ?? []), ...policies])]

      return new Promise((resolve) => {
        setPending({
          policies: union,
          settle: (result) => {
            setPending(null)

            if (result !== null) {
              setAuthorization(result)
            }

            resolve(result)
          },
        })
      })
    },
    [],
  )

  const clear = useCallback(() => {
    setAuthorization(null)
  }, [])

  const value = useMemo<OverrideContextValue>(
    () => ({ authorization, authorize, clear }),
    [authorization, authorize, clear],
  )

  return (
    <OverrideContext value={value}>
      {children}
      {pending !== null ? (
        <ManagerAuthorizationDialog policies={pending.policies} onSettle={pending.settle} />
      ) : null}
    </OverrideContext>
  )
}
