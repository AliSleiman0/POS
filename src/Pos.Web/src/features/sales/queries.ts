/**
 * Server state for receipts and sale history.
 */

import { useQuery } from '@tanstack/react-query'
import { api, unwrap } from '@/api/client'
import type { components } from '@/api/schema'

export type Receipt = components['schemas']['ReceiptResponse']

export const salesKeys = {
  receipt: (saleId: string) => ['sales', saleId, 'receipt'] as const,
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
      unwrap(
        api.GET('/api/v1/sales/{id}/receipt', { params: { path: { id: saleId ?? '' } } }),
      ),
    enabled: saleId !== null,
    staleTime: Infinity,
  })
}
