/**
 * The cart context and its hook.
 *
 * Separate from `CartProvider.tsx` for the same reason `authContext.ts` is
 * separate from `AuthProvider.tsx`: a module that exports both a component and a
 * hook breaks Fast Refresh, and on a till that means losing the cart on screen
 * every time the file is saved.
 */

import { createContext, use, type ActionDispatch } from 'react'
import type { Cart, CartAction } from './cart'

export interface CartContextValue {
  cart: Cart
  dispatch: ActionDispatch<[action: CartAction]>
}

export const CartContext = createContext<CartContextValue | null>(null)

export function useCart(): CartContextValue {
  const context = use(CartContext)

  if (context === null) {
    throw new Error('useCart must be used inside a <CartProvider>')
  }

  return context
}
