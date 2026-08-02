import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { EmptyState, ErrorState, LoadingState } from '@/components/states'
import { formatQuantity } from '@/lib/money'
import { StockAdjustmentDialog } from './StockAdjustmentDialog'
import { StockMovementsPanel } from './StockMovementsPanel'
import { useStockLevels } from './queries'

/**
 * Stock levels, with an adjustment form and the ledger behind each row.
 */
export function StockPage() {
  const [search, setSearch] = useState('')
  const [adjusting, setAdjusting] = useState<{ id: string; name: string } | null>(null)
  const [viewing, setViewing] = useState<{ id: string; name: string } | null>(null)

  const stock = useStockLevels(search)
  const rows = stock.data?.pages.flatMap((page) => page.items) ?? []

  return (
    <div className="mx-auto flex max-w-4xl flex-col gap-6 p-6">
      <header>
        <h1 className="text-xl font-semibold text-foreground">Stock</h1>
        <p className="mt-1 text-sm text-muted-foreground">
          On-hand figures are read live. Every change is a movement in an append-only ledger —
          nothing here edits a past number.
        </p>
      </header>

      <Input
        aria-label="Search stock"
        placeholder="Search by name or SKU"
        className="max-w-xs"
        value={search}
        onChange={(event) => {
          setSearch(event.target.value)
        }}
      />

      {stock.isPending ? (
        <LoadingState label="Loading stock levels…" />
      ) : stock.isError ? (
        <ErrorState
          error={stock.error}
          onRetry={() => {
            void stock.refetch()
          }}
          title="Could not load the stock levels."
        />
      ) : rows.length === 0 ? (
        <EmptyState
          title={search === '' ? 'Nothing is stock-tracked yet.' : 'Nothing matched.'}
          description={
            search === ''
              ? 'A product tracks stock when "Track stock" is ticked on its form.'
              : undefined
          }
        />
      ) : (
        <>
          <div className="overflow-x-auto rounded-lg border border-border">
            <table className="w-full text-sm">
              <thead className="bg-muted text-left text-xs text-muted-foreground">
                <tr>
                  <th className="px-3 py-2 font-medium">SKU</th>
                  <th className="px-3 py-2 font-medium">Name</th>
                  <th className="px-3 py-2 text-right font-medium">On hand</th>
                  <th className="px-3 py-2" />
                </tr>
              </thead>
              <tbody>
                {rows.map((item) => (
                  <tr key={item.id} className="border-t border-border">
                    <td className="px-3 py-2 font-mono text-xs">{item.sku}</td>
                    <td className="px-3 py-2">{item.name}</td>
                    <td className="px-3 py-2 text-right tabular-nums">
                      <span
                        className={
                          Number(item.onHand) < 0
                            ? 'font-medium text-destructive'
                            : item.belowReorderPoint
                              ? 'font-medium text-foreground'
                              : undefined
                        }
                      >
                        {formatQuantity(item.onHand)}
                      </span>
                      {item.belowReorderPoint ? (
                        <span className="ml-2 text-xs text-muted-foreground">low</span>
                      ) : null}
                    </td>
                    <td className="px-3 py-2 text-right">
                      <div className="flex justify-end gap-2">
                        <Button
                          variant="ghost"
                          size="sm"
                          onClick={() => {
                            setViewing({ id: item.id, name: item.name })
                          }}
                        >
                          Ledger
                        </Button>
                        <Button
                          variant="outline"
                          size="sm"
                          onClick={() => {
                            setAdjusting({ id: item.id, name: item.name })
                          }}
                        >
                          Adjust
                        </Button>
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>

          {stock.hasNextPage ? (
            <Button
              variant="outline"
              className="self-center"
              disabled={stock.isFetchingNextPage}
              onClick={() => {
                void stock.fetchNextPage()
              }}
            >
              {stock.isFetchingNextPage ? 'Loading…' : 'Load more'}
            </Button>
          ) : null}
        </>
      )}

      {adjusting !== null ? (
        <StockAdjustmentDialog
          productId={adjusting.id}
          productName={adjusting.name}
          onClose={() => {
            setAdjusting(null)
          }}
        />
      ) : null}

      {viewing !== null ? (
        <StockMovementsPanel
          productId={viewing.id}
          productName={viewing.name}
          onClose={() => {
            setViewing(null)
          }}
        />
      ) : null}
    </div>
  )
}
