/**
 * When it is safe to swap the till's code underneath it.
 *
 * Pulled out of `UpdatePrompt` as a plain function so it can be exercised
 * without a service worker: `virtual:pwa-register` exists only in a PWA build,
 * so a component test would silently take the "no worker here" branch and
 * assert nothing at all. This is the rule, and it is the part worth testing.
 *
 * **Why a rule is needed.** A service worker that takes over mid-sale can
 * change the pricing code between the quote a cashier read out and the sale
 * that gets written. The customer is told one number and charged from another,
 * and the receipt is the evidence they were right to dispute it.
 */

import { isEmpty, type Cart } from '@/features/register/cart'
import type { SaleInFlight } from '@/features/register/storage'

/**
 * Whether the till is between customers.
 *
 * Three conditions, and each one covers a case the others miss:
 *
 * - **The cart is empty.** Twenty scanned items are twenty a cashier would have
 *   to ring again.
 * - **No sale identity has been minted.** `saleKey` is set the moment tendering
 *   begins, and it survives backing out to edit a line — so an empty cart with
 *   a key is a sale in progress that happens to have had its last line removed.
 * - **Nothing is in flight.** A payment was submitted and its answer has not
 *   been seen. Reloading here throws away the till's own record of it, and the
 *   recovery that record exists for is precisely the case where a customer has
 *   already handed over money.
 */
export function isTillIdle(cart: Cart, inFlight: SaleInFlight | null): boolean {
  return isEmpty(cart) && cart.saleKey === null && inFlight === null
}
