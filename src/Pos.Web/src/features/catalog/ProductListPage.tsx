import { useState } from 'react'
import { Link } from 'react-router'
import { Button, buttonVariants } from '@/components/ui/button'
import { Input, Select } from '@/components/ui/input'
import { EmptyState, ErrorState, LoadingState } from '@/components/states'
import { useAuth } from '@/auth/authContext'
import { formatMoney } from '@/lib/money'
import { useAllCategories, useProducts, type ProductFilters } from './queries'

/**
 * The catalog list: search, filter, and page on the cursor.
 */
export function ProductListPage() {
  const { tenant } = useAuth()
  const currency = tenant?.currencyCode ?? 'GBP'

  const [filters, setFilters] = useState<ProductFilters>({
    q: '',
    categoryId: '',
    activeOnly: true,
  })

  const categories = useAllCategories()
  const products = useProducts(filters)

  const rows = products.data?.pages.flatMap((page) => page.items) ?? []
  const searching = filters.q !== '' || filters.categoryId !== ''

  return (
    <div className="mx-auto flex max-w-5xl flex-col gap-6 p-6">
      <header className="flex items-center justify-between gap-4">
        <h1 className="text-xl font-semibold text-foreground">Products</h1>
        <Link to="/catalog/products/new" className={buttonVariants()}>
          New product
        </Link>
      </header>

      <div className="flex flex-wrap items-end gap-3">
        <Input
          aria-label="Search products"
          placeholder="Search by name or SKU"
          className="max-w-xs"
          value={filters.q}
          onChange={(event) => {
            setFilters((current) => ({ ...current, q: event.target.value }))
          }}
        />

        <Select
          aria-label="Filter by category"
          className="max-w-48"
          value={filters.categoryId}
          onChange={(event) => {
            setFilters((current) => ({ ...current, categoryId: event.target.value }))
          }}
        >
          <option value="">All categories</option>
          {(categories.data ?? []).map((category) => (
            <option key={category.id} value={category.id}>
              {category.name}
            </option>
          ))}
        </Select>

        <label className="flex h-9 items-center gap-2 text-sm text-muted-foreground">
          <input
            type="checkbox"
            className="size-4"
            checked={!filters.activeOnly}
            onChange={(event) => {
              setFilters((current) => ({ ...current, activeOnly: !event.target.checked }))
            }}
          />
          Include deactivated
        </label>
      </div>

      {products.isPending ? (
        <LoadingState label="Loading the catalog…" />
      ) : products.isError ? (
        <ErrorState
          error={products.error}
          onRetry={() => {
            void products.refetch()
          }}
          title="Could not load the catalog."
        />
      ) : rows.length === 0 ? (
        searching ? (
          <EmptyState
            title="Nothing matched."
            description="Try a shorter search, or clear the category filter."
          />
        ) : (
          // The first thing a brand-new tenant sees. A blank panel here reads
          // as a broken screen rather than as "you have not added anything".
          <EmptyState
            title="No products yet."
            description="Add your first product and it will be scannable at the till straight away."
            action={
              <Link to="/catalog/products/new" className={buttonVariants()}>
                Add a product
              </Link>
            }
          />
        )
      ) : (
        <>
          <div className="overflow-x-auto rounded-lg border border-border">
            <table className="w-full text-sm">
              <thead className="bg-muted text-left text-xs text-muted-foreground">
                <tr>
                  <th className="px-3 py-2 font-medium">SKU</th>
                  <th className="px-3 py-2 font-medium">Name</th>
                  <th className="px-3 py-2 text-right font-medium">Price</th>
                  <th className="px-3 py-2 font-medium">Unit</th>
                  <th className="px-3 py-2 font-medium">Status</th>
                </tr>
              </thead>
              <tbody>
                {rows.map((product) => (
                  <tr key={product.id} className="border-t border-border">
                    <td className="px-3 py-2 font-mono text-xs">{product.sku}</td>
                    <td className="px-3 py-2">
                      <Link
                        to={`/catalog/products/${product.id}`}
                        className="font-medium text-foreground underline-offset-4 hover:underline"
                      >
                        {product.name}
                      </Link>
                    </td>
                    <td className="px-3 py-2 text-right tabular-nums">
                      {formatMoney(product.unitPrice, currency)}
                    </td>
                    <td className="px-3 py-2 text-muted-foreground">{product.unit}</td>
                    <td className="px-3 py-2">
                      {product.isActive ? (
                        <span className="text-muted-foreground">Active</span>
                      ) : (
                        <span className="font-medium text-destructive">Deactivated</span>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>

          {products.hasNextPage ? (
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
        </>
      )}
    </div>
  )
}
