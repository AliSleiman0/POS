import { useMemo, useReducer, type ReactNode } from 'react'
import { cartReducer, EMPTY_CART } from './cart'
import { CartContext, type CartContextValue } from './cartContext'

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
 * 5.5 adds `sessionStorage` persistence and the sale's idempotency key here, so
 * a reload mid-submit recovers rather than re-charging. The shape is chosen now
 * so that is an addition rather than a move.
 */
export function CartProvider({ children }: { children: ReactNode }) {
  const [cart, dispatch] = useReducer(cartReducer, EMPTY_CART)

  // `dispatch` is stable, so this changes only when the cart does.
  const value = useMemo<CartContextValue>(() => ({ cart, dispatch }), [cart])

  return <CartContext value={value}>{children}</CartContext>
}
