import { useEffect, useState, type RefObject } from 'react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { EmptyState, ErrorState, LoadingState } from '@/components/states'
import { formatMoney } from '@/lib/money'
import { useProducts } from '@/features/catalog/queries'
import { useOffline } from '@/features/offline/offlineContext'
import { searchProducts } from '@/lib/offline/catalog'
import type { MirroredProduct } from '@/lib/offline/db'
import type { CartProduct } from './cart'

/**
 * The unlabelled goods.
 *
 * Loose produce, bakery, anything that never had a barcode on it. Reuses the
 * catalog's own paged query rather than a second one — with `activeOnly: true`,
 * because a deactivated product must not be sellable from here even though it
 * still scans (the server refuses it either way).
 *
 * Tiles rather than a table: this is tapped with a finger on a counter tablet,
 * and a row in a dense list is a mis-tap.
 */
/** A row the grid can render, from either source. */
interface DisplayProduct {
  id: string
  name: string
  sku: string
  unit: CartProduct['unit']
  unitPrice: string | number
}

export function ProductGrid({
  search,
  onSearchChange,
  searchRef,
  currency,
  onPick,
}: {
  search: string
  onSearchChange: (value: string) => void
  searchRef: RefObject<HTMLInputElement | null>
  currency: string
  onPick: (product: CartProduct) => void
}) {
  const offline = useOffline()
  const isOffline = offline.connectivity === 'offline'

  /*
   * The server's list while online, the mirror's while not.
   *
   * Not mirror-first, unlike a scan — and the asymmetry is deliberate. A scan
   * resolves one known code, where the mirror is both faster and no less
   * correct. This is a *browse*: the catalog admin screen may have added a
   * product a minute ago, and a grid that preferred the mirror would keep it
   * invisible until the next sync for no benefit at all.
   *
   * The query is disabled while offline so TanStack Query does not spend the
   * page's retries on a server nobody can reach.
   */
  const products = useProducts({ q: search, categoryId: '', activeOnly: true }, !isOffline)
  const [mirrored, setMirrored] = useState<MirroredProduct[] | null>(null)

  useEffect(() => {
    if (!isOffline || offline.db === null) {
      setMirrored(null)
      return
    }

    let cancelled = false

    void searchProducts(offline.db, search).then((found) => {
      if (!cancelled) {
        setMirrored(found)
      }
    })

    return () => {
      cancelled = true
    }
  }, [isOffline, offline.db, search])

  const rows: DisplayProduct[] = isOffline
    ? (mirrored ?? []).map((product) => ({
        id: product.id,
        name: product.name,
        sku: product.sku,
        unit: product.unit as CartProduct['unit'],
        unitPrice: product.unitPrice,
      }))
    : (products.data?.pages.flatMap((page) => page.items) ?? [])

  return (
    <section
      aria-label="Products"
      // `flex-1` so the grid takes the height the total panel does not, rather
      // than leaving a band of dead screen under it on a desktop till.
      className="flex min-h-0 flex-1 flex-col gap-3 rounded-xl border border-border bg-card p-3"
    >
      <Input
        ref={searchRef}
        aria-label="Search products"
        placeholder="Search products — F2"
        value={search}
        onChange={(event) => {
          onSearchChange(event.target.value)
        }}
      />

      <div className="min-h-0 flex-1 overflow-y-auto">
        {isOffline && mirrored === null ? (
          <LoadingState label="Reading this till's catalog…" />
        ) : !isOffline && products.isPending ? (
          <LoadingState label="Loading products…" />
        ) : !isOffline && products.isError ? (
          <ErrorState
            error={products.error}
            title="Could not load the products."
            onRetry={() => {
              void products.refetch()
            }}
          />
        ) : rows.length === 0 ? (
          <EmptyState
            title={search === '' ? 'No products yet.' : 'Nothing matched.'}
            description={
              isOffline
                ? 'This till is offline and searching its own copy of the catalog, which may be incomplete.'
                : search === ''
                  ? 'Add products in the catalog and they appear here.'
                  : 'Try a shorter search.'
            }
          />
        ) : (
          <ul className="grid grid-cols-2 gap-2 xl:grid-cols-3">
            {rows.map((product) => (
              <li key={product.id}>
                <button
                  type="button"
                  onClick={() => {
                    onPick({
                      productId: product.id,
                      name: product.name,
                      sku: product.sku,
                      unit: product.unit,
                      unitPrice: product.unitPrice,
                    })
                  }}
                  // Tall enough for a finger: 44px is the floor and this is
                  // comfortably past it, because the alternative is charging
                  // for the item next to the one they meant.
                  className="flex h-20 w-full flex-col justify-between rounded-lg border border-border bg-background p-2 text-left transition-colors hover:bg-muted focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50 focus-visible:outline-none"
                >
                  <span className="line-clamp-2 text-sm font-medium text-foreground">
                    {product.name}
                  </span>
                  <span className="text-sm font-semibold tabular-nums text-muted-foreground">
                    {formatMoney(product.unitPrice, currency)}
                  </span>
                </button>
              </li>
            ))}
          </ul>
        )}
      </div>

      {!isOffline && products.hasNextPage ? (
        <Button
          variant="outline"
          className="self-center"
          disabled={products.isFetchingNextPage}
          onClick={() => {
            void products.fetchNextPage()
          }}
        >
          {products.isFetchingNextPage ? 'Loading…' : 'Load more'}
        </Button>
      ) : null}
    </section>
  )
}
