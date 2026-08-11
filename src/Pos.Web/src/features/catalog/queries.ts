/**
 * Catalog server state.
 *
 * One place for the query keys, so an invalidation after a write cannot miss a
 * list that is showing the row it changed.
 */

import { useInfiniteQuery, useQuery } from '@tanstack/react-query'
import { api, unwrap } from '@/api/client'
import { LIVE_QUERY_OPTIONS } from '@/app/queryClient'

export const catalogKeys = {
  products: (filters: ProductFilters) => ['products', filters] as const,
  product: (id: string) => ['products', id] as const,
  barcodes: (productId: string) => ['products', productId, 'barcodes'] as const,
  categories: () => ['categories'] as const,
  taxClasses: () => ['tax-classes'] as const,
  stock: (search: string) => ['stock', search] as const,
  movements: (productId: string) => ['stock', productId, 'movements'] as const,
}

export interface ProductFilters {
  q: string
  categoryId: string
  /** `false` shows deactivated products too. */
  activeOnly: boolean
}

/**
 * The product list, paged.
 *
 * Cursor pagination, not offset: offsets skip and duplicate rows when data is
 * written underneath them, which is normal during trading hours. The cursor is
 * opaque and is never parsed — its format carries a version so it can change.
 */
/**
 * @param enabled `false` stands the query down entirely — used by the register's
 * grid while the till is offline, so TanStack Query does not spend its retries
 * on a server nobody can reach and the grid can render the local mirror instead.
 */
export function useProducts(filters: ProductFilters, enabled = true) {
  return useInfiniteQuery({
    enabled,
    queryKey: catalogKeys.products(filters),
    initialPageParam: undefined as string | undefined,
    queryFn: ({ pageParam }) =>
      unwrap(
        api.GET('/api/v1/products', {
          params: {
            query: {
              cursor: pageParam,
              q: filters.q === '' ? undefined : filters.q,
              categoryId: filters.categoryId === '' ? undefined : filters.categoryId,
              // Sent explicitly. The server defaults it to `true` so the
              // register grid can never offer an unsellable product; the admin
              // screen is the one place that deliberately asks for everything.
              activeOnly: filters.activeOnly,
            },
          },
        }),
      ),
    getNextPageParam: (last) => last.nextCursor ?? undefined,
  })
}

export function useProduct(id: string) {
  return useQuery({
    queryKey: catalogKeys.product(id),
    queryFn: () => unwrap(api.GET('/api/v1/products/{id}', { params: { path: { id } } })),
  })
}

export function useBarcodes(productId: string) {
  return useQuery({
    queryKey: catalogKeys.barcodes(productId),
    queryFn: () =>
      unwrap(api.GET('/api/v1/products/{id}/barcodes', { params: { path: { id: productId } } })),
  })
}

/**
 * Every category, flattened across pages.
 *
 * A shop has tens of categories, not thousands, and every product form needs
 * the whole list in a dropdown — so paging it into the UI would be ceremony
 * around a single screenful.
 */
export function useAllCategories() {
  return useQuery({
    queryKey: catalogKeys.categories(),
    queryFn: async () => {
      const all = []
      let cursor: string | undefined

      do {
        const page = await unwrap(
          api.GET('/api/v1/categories', { params: { query: { cursor, limit: 100 } } }),
        )
        all.push(...page.items)
        cursor = page.nextCursor ?? undefined
      } while (cursor !== undefined)

      return all
    },
  })
}

export function useAllTaxClasses() {
  return useQuery({
    queryKey: catalogKeys.taxClasses(),
    queryFn: async () => {
      const all = []
      let cursor: string | undefined

      do {
        const page = await unwrap(
          api.GET('/api/v1/tax-classes', { params: { query: { cursor, limit: 100 } } }),
        )
        all.push(...page.items)
        cursor = page.nextCursor ?? undefined
      } while (cursor !== undefined)

      return all
    },
  })
}

/** Stock levels. Live, not cached: an on-hand figure drives an adjustment decision. */
export function useStockLevels(search: string) {
  return useInfiniteQuery({
    queryKey: catalogKeys.stock(search),
    initialPageParam: undefined as string | undefined,
    queryFn: ({ pageParam }) =>
      unwrap(
        api.GET('/api/v1/stock', {
          params: { query: { cursor: pageParam, q: search === '' ? undefined : search } },
        }),
      ),
    getNextPageParam: (last) => last.nextCursor ?? undefined,
    ...LIVE_QUERY_OPTIONS,
  })
}

/** A product's ledger. Append-only, so what is here never changes retroactively. */
export function useStockMovements(productId: string) {
  return useInfiniteQuery({
    queryKey: catalogKeys.movements(productId),
    initialPageParam: undefined as string | undefined,
    queryFn: ({ pageParam }) =>
      unwrap(
        api.GET('/api/v1/stock/{productId}/movements', {
          params: { path: { productId }, query: { cursor: pageParam } },
        }),
      ),
    getNextPageParam: (last) => last.nextCursor ?? undefined,
    ...LIVE_QUERY_OPTIONS,
  })
}
