/**
 * The register's server state: the drawer, and the price of what is in the cart.
 *
 * Both are money-adjacent, so both take `LIVE_QUERY_OPTIONS` — the 60-second
 * catalog staleness is for product names, not for whether a till may sell.
 */

import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, unwrap } from '@/api/client'
import { LIVE_QUERY_OPTIONS } from '@/app/queryClient'
import { getEnrolledRegisterId } from '@/auth/deviceToken'
import { cartSignature, isEmpty, toSaleLines, type Cart } from './cart'

export const registerKeys = {
  currentShift: (registerId: string | null) => ['shifts', 'current', registerId] as const,
  quote: (signature: string) => ['sales', 'quote', signature] as const,
  barcode: (code: string) => ['products', 'by-barcode', code] as const,
}

/**
 * The open shift for this till, if there is one.
 *
 * **A 404 is the answer, not a failure.** `GET /shifts/current` says "no drawer
 * is open" that way (docs/API.md), so `isError` here means "closed" and is not
 * retried — three retries of a 404 delay the open-shift prompt for no reason.
 *
 * The register id comes from `localStorage`, written at enrolment. Reading it
 * off the access token would work only after a *PIN* login, so a manager signed
 * in by password at the same till would see no drawer at all.
 */
export function useCurrentShift() {
  const registerId = getEnrolledRegisterId()

  const query = useQuery({
    queryKey: registerKeys.currentShift(registerId),
    queryFn: () =>
      unwrap(
        api.GET('/api/v1/shifts/current', {
          params: { query: { registerId: registerId ?? undefined } },
        }),
      ),
    ...LIVE_QUERY_OPTIONS,
    enabled: registerId !== null,
    retry: false,
  })

  return { ...query, registerId }
}

/**
 * Opens the drawer for the day.
 *
 * The idempotency key is the caller's, minted once for the panel and reused on
 * every retry (CLAUDE.md invariant 6) — a key generated in here would be new per
 * attempt, and a timeout followed by a retry would open a second shift.
 */
export function useOpenShift(idempotencyKey: string) {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: (input: { registerId: string; openingFloat: number }) =>
      unwrap(
        api.POST('/api/v1/shifts', {
          params: { header: { 'Idempotency-Key': idempotencyKey } },
          body: { registerId: input.registerId, openingFloat: input.openingFloat },
        }),
      ),
    onSuccess: async () => {
      // Both the register and the header indicator read the same key.
      await queryClient.invalidateQueries({ queryKey: ['shifts'] })
    },
  })
}

/**
 * What the cart costs, according to the server.
 *
 * `POST /sales/quote` prices a cart and writes nothing — no register, no shift,
 * no tenders and no idempotency key (docs/API.md). It shares `toSaleLines` with
 * the sale itself, so what is displayed and what is sold cannot diverge.
 *
 * Keyed on the cart's *signature* rather than the cart object: selecting a
 * different line changes the object but not the money, and re-pricing on a
 * cursor move would put a spinner over the total every time a cashier looked at
 * a line.
 *
 * A held manager grant is presented on every quote. The server validates it
 * without spending it — the register re-quotes on every keystroke, and a
 * single-use grant consumed by the first of those would leave nothing for the
 * sale it was minted for.
 */
export function useQuote(cart: Cart, grant: string | null) {
  const signature = cartSignature(cart)
  const lines = toSaleLines(cart)

  return useQuery({
    // The grant is not in the key. Two carts priced identically are the same
    // question whoever authorised them, and keying on a credential would put
    // one in the query cache.
    queryKey: registerKeys.quote(signature),
    queryFn: () =>
      unwrap(
        api.POST('/api/v1/sales/quote', {
          params: grant === null ? {} : { header: { 'X-Override-Authorization': grant } },
          body: {
            clientTransactionId: null,
            registerId: null,
            shiftId: null,
            lines,
            cartDiscountAmount: cart.cartDiscountAmount,
            tenders: null,
          },
        }),
      ),
    enabled: !isEmpty(cart),
    ...LIVE_QUERY_OPTIONS,
    // The previous total stays on screen while the next one is computed, so the
    // most prominent number on the till does not blink on every scan.
    placeholderData: (previous) => previous,
  })
}
