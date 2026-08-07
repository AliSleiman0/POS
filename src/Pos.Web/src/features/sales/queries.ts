/**
 * Server state for receipts and sale history.
 */

import { useInfiniteQuery, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, unwrap } from '@/api/client'
import type { components } from '@/api/schema'
import { LIVE_QUERY_OPTIONS } from '@/app/queryClient'

export type Receipt = components['schemas']['ReceiptResponse']
export type Sale = components['schemas']['SaleResponse']
export type SaleSummary = components['schemas']['SaleSummaryResponse']

/** What the history list is filtered by. Empty strings mean "no filter". */
export interface SaleFilters {
  saleNumber: string
  from: string
  to: string
  type: string
  status: string
  registerId: string
  cashierId: string
}

export const EMPTY_FILTERS: SaleFilters = {
  saleNumber: '',
  from: '',
  to: '',
  type: '',
  status: '',
  registerId: '',
  cashierId: '',
}

export const salesKeys = {
  receipt: (saleId: string) => ['sales', saleId, 'receipt'] as const,
  list: (filters: SaleFilters) => ['sales', 'list', filters] as const,
  detail: (saleId: string) => ['sales', 'detail', saleId] as const,
}

/**
 * The receipt payload for a sale.
 *
 * **Fetched, not composed.** Everything on the paper — the tax breakdown, the
 * shop's address, the timestamp in the tenant's zone — is decided by
 * `ReceiptBuilder` on the server. A client that assembled any of it would be a
 * second renderer, and the whole point of the endpoint is that there is one.
 *
 * A completed sale is append-only, so its receipt cannot change: `staleTime`
 * is `Infinity` and a reprint of a sale already fetched costs no round trip.
 * The one thing that does move is `issuedAtLocal`, which stamps the copy — so
 * a reprint taken from cache carries the moment the payload was fetched rather
 * than the moment the button was pressed. That is close enough to be honest and
 * far better than refetching a fixed document on every open.
 */
export function useReceipt(saleId: string | null) {
  return useQuery({
    queryKey: salesKeys.receipt(saleId ?? ''),
    queryFn: () =>
      unwrap(api.GET('/api/v1/sales/{id}/receipt', { params: { path: { id: saleId ?? '' } } })),
    enabled: saleId !== null,
    staleTime: Infinity,
  })
}

/**
 * The sale history, filtered and paged.
 *
 * **A sale number is a lookup, not a filter among six.** It is the reference a
 * customer reads off the paper in their hand, and it is the only search that
 * happens at a counter — so an empty result for one is "no such sale" rather
 * than "nothing matched your filters".
 *
 * `from`/`to` are trading days and the server resolves them through the tenant's
 * zone and day-start offset, the same way the daily report does. Sending a UTC
 * date here would make the history and the report disagree by a few hours.
 */
export function useSaleHistory(filters: SaleFilters) {
  return useInfiniteQuery({
    queryKey: salesKeys.list(filters),
    initialPageParam: undefined as string | undefined,
    queryFn: ({ pageParam }) =>
      unwrap(
        api.GET('/api/v1/sales', {
          params: {
            query: {
              cursor: pageParam,
              saleNumber: filters.saleNumber === '' ? undefined : Number(filters.saleNumber),
              from: blank(filters.from),
              to: blank(filters.to),
              type: blank(filters.type),
              status: blank(filters.status),
              registerId: blank(filters.registerId),
              cashierId: blank(filters.cashierId),
            },
          },
        }),
      ),
    getNextPageParam: (last) => last.nextCursor ?? undefined,
    ...LIVE_QUERY_OPTIONS,
  })
}

/** One sale, with its lines, tenders and both halves of any refund link. */
export function useSale(saleId: string | null) {
  return useQuery({
    queryKey: salesKeys.detail(saleId ?? ''),
    queryFn: () =>
      unwrap(api.GET('/api/v1/sales/{id}', { params: { path: { id: saleId ?? '' } } })),
    enabled: saleId !== null,
    ...LIVE_QUERY_OPTIONS,
  })
}

/**
 * Refunds a sale, in full or line by line.
 *
 * **The idempotency key is the caller's**, minted when the dialog opens and
 * reused on every attempt (CLAUDE.md invariant 6). A key generated in here would
 * be new per press, so a timeout followed by a retry would pay the customer
 * twice — which is the exact failure this header exists to prevent, and the one
 * `StockAdjustmentDialog` already gets right.
 */
export function useRefundSale(idempotencyKey: string) {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: (input: {
      saleId: string
      registerId: string
      shiftId: string
      reason: string
      lines: { saleLineId: string; quantity: number }[] | null
    }) =>
      unwrap(
        api.POST('/api/v1/sales/{id}/refund', {
          params: {
            path: { id: input.saleId },
            header: { 'Idempotency-Key': idempotencyKey },
          },
          body: {
            // The same GUID in both places, as POST /sales does: the key is the
            // domain's idea of this operation, not just a transport header.
            clientTransactionId: idempotencyKey,
            registerId: input.registerId,
            shiftId: input.shiftId,
            reason: input.reason,
            lines: input.lines,
          },
        }),
      ),

    onSuccess: async () => {
      // The original's refund list, the history, the drawer and the stock that
      // came back all moved.
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ['sales'] }),
        queryClient.invalidateQueries({ queryKey: ['shifts'] }),
        queryClient.invalidateQueries({ queryKey: ['stock'] }),
        queryClient.invalidateQueries({ queryKey: ['reports'] }),
      ])
    },
  })
}

/** An empty filter is an absent parameter, not an empty one. */
function blank(value: string): string | undefined {
  return value === '' ? undefined : value
}
