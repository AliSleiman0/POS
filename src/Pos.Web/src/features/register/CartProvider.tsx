import { useEffect, useMemo, useReducer, type ReactNode } from 'react'
import { cartReducer, EMPTY_CART, type Cart } from './cart'
import { CartContext, type CartContextValue } from './cartContext'
import { readCart, writeCart } from './storage'

/**
 * Holds the cart above the router's outlet.
 *
 * **Where this is mounted is the design.** It sits inside `AppLayout`, which
 * sits inside `RequireAuth` — a tree that is deliberately *not* unmounted when a
 * session expires (`RequireAuth` renders the re-auth prompt over the current
 * route instead of navigating). So a token expiring mid-sale costs a password,
 * not the cart, and a cashier who taps Catalog to check a price comes back to
 * the same six items.
 *
 * A cart inside `RegisterPage` would be thrown away by both.
 *
 * **The cart is persisted to `sessionStorage` on every change**, which covers
 * two different failures with one mechanism. An accidental F5 with twenty items
 * scanned no longer costs a re-scan; and, because `saleKey` is part of the cart,
 * a reload *during* a payment comes back holding the GUID that makes the retry
 * safe rather than a fresh one that would charge the customer twice.
 *
 * The manager's override grant is **not** covered by this and must not be. It is
 * in `OverrideProvider` as React state, so a reload loses it — the right outcome
 * for a five-minute credential on a shared tablet, and pinned by a test.
 */
export function CartProvider({ children }: { children: ReactNode }) {
  // Lazy init rather than an effect that fills it in afterwards: the first paint
  // already has the restored basket, instead of showing an empty till for a
  // frame and then populating it under the cashier's eyes.
  const [cart, dispatch] = useReducer(cartReducer, EMPTY_CART, restore)

  useEffect(() => {
    writeCart(cart)
  }, [cart])

  // `dispatch` is stable, so this changes only when the cart does.
  const value = useMemo<CartContextValue>(() => ({ cart, dispatch }), [cart])

  return <CartContext value={value}>{children}</CartContext>
}

function restore(fallback: Cart): Cart {
  return readCart() ?? fallback
}
