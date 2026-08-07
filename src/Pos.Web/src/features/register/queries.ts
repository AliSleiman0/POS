/**
 * The register's server state: the drawer, and the price of what is in the cart.
 *
 * Both are money-adjacent, so both take `LIVE_QUERY_OPTIONS` — the 60-second
 * catalog staleness is for product names, not for whether a till may sell.
 */

import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, unwrap, unwrapWithResponse } from '@/api/client'
import { wasReplayed } from '@/api/idempotency'
import { isProblemError } from '@/api/problem'
import type { components } from '@/api/schema'
import { LIVE_QUERY_OPTIONS } from '@/app/queryClient'
import { getEnrolledRegisterId } from '@/auth/deviceToken'
import { cartSignature, isEmpty, toSaleLines, type Cart } from './cart'
import type { Tender } from './tender'

export const registerKeys = {
  currentShift: (registerId: string | null) => ['shifts', 'current', registerId] as const,
  quote: (signature: string) => ['sales', 'quote', signature] as const,
  barcode: (code: string) => ['products', 'by-barcode', code] as const,
  byClientTransaction: (saleKey: string) => ['sales', 'by-client-transaction', saleKey] as const,
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

/**
 * How the till came to be holding this sale.
 *
 * Three different sentences for a cashier, and the distinction matters most to
 * whoever reconciles the drawer at the end of the day:
 *
 * - `fresh` — this press wrote it.
 * - `replayed` — the server had seen the key. What a successful retry looks
 *   like, and the only evidence that a second press did not take a second
 *   payment.
 * - `recovered` — it was found afterwards, by asking what the key bought. The
 *   till never saw the original answer.
 */
export type SaleProvenance = 'fresh' | 'replayed' | 'recovered'

/** A completed sale, and how the till learned about it. */
export interface CompletedSale {
  sale: components['schemas']['SaleResponse']
  provenance: SaleProvenance
}

/**
 * What a client transaction id bought, if it bought anything.
 *
 * **A read, deliberately.** The till calls this after a reload interrupted a
 * payment, holding the GUID it submitted and nothing else. Re-POSTing would
 * answer the same question by *doing* the thing — fine when the sale landed, and
 * a charge nobody authorised when it did not.
 *
 * `null` for a 404, which is not a failure here: it is the answer "nothing was
 * taken", and it is half the reason the endpoint exists.
 */
export async function fetchSaleByKey(
  saleKey: string,
): Promise<components['schemas']['SaleResponse'] | null> {
  try {
    return await unwrap(
      api.GET('/api/v1/sales/by-client-transaction/{clientTransactionId}', {
        params: { path: { clientTransactionId: saleKey } },
      }),
    )
  } catch (caught) {
    if (isProblemError(caught) && caught.status === 404) {
      return null
    }

    // Anything else — offline, a 500 — is *not* an answer, and must not be
    // rendered as one. The caller says so rather than guessing.
    throw caught
  }
}

/**
 * Turns the cart into a sale.
 *
 * **The idempotency key comes from the cart, not from here** (CLAUDE.md
 * invariant 6). Minted when tendering began and reused on every attempt, so a
 * timeout followed by a retry returns the original sale rather than taking the
 * money twice. A key generated inside this function would be new each time and
 * the header would be decorative — see `cart.ts`'s `beginSale`.
 *
 * The same GUID is sent as `clientTransactionId`, per docs/API.md.
 *
 * Lines go through `toSaleLines`, which is also what the quote uses. That is the
 * mechanical reason the figure on the screen and the figure on the sale cannot
 * disagree — not a promise that two code paths were kept in step.
 */
export function useCompleteSale() {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: async (input: {
      cart: Cart
      registerId: string
      shiftId: string
      tenders: readonly Tender[]
      /** A manager's grant, when the cart carries something needing one. */
      grant: string | null
    }): Promise<CompletedSale> => {
      const saleKey = input.cart.saleKey

      if (saleKey === null) {
        // A programming error, not a user-facing one: nothing may reach this
        // without `beginSale` having run, because without a key the retry story
        // is gone and a double-tap is a double charge.
        throw new Error('The sale has no idempotency key — dispatch beginSale first.')
      }

      const { data, response } = await unwrapWithResponse(
        api.POST('/api/v1/sales', {
          params: {
            header: {
              'Idempotency-Key': saleKey,
              ...(input.grant === null ? {} : { 'X-Override-Authorization': input.grant }),
            },
          },
          body: {
            clientTransactionId: saleKey,
            registerId: input.registerId,
            shiftId: input.shiftId,
            lines: toSaleLines(input.cart),
            cartDiscountAmount: input.cart.cartDiscountAmount,
            tenders: input.tenders.map((tender) => ({
              method: 'Cash',
              // Back to a decimal for the wire. Held as integer minor units
              // right up to here so nothing accumulated float error on the way.
              amount: tender.amountMinor / 100,
              reference: null,
            })),
          },
        }),
      )

      return { sale: data, provenance: wasReplayed(response) ? 'replayed' : 'fresh' }
    },

    onSuccess: async () => {
      // Both moved: the drawer's expected cash, and the on-hand of everything
      // that was sold.
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ['shifts'] }),
        queryClient.invalidateQueries({ queryKey: ['stock'] }),
      ])
    },
  })
}
