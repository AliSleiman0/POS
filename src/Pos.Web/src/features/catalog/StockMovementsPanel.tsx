import { Button } from '@/components/ui/button'
import { EmptyState, ErrorState, LoadingState } from '@/components/states'
import { formatQuantity } from '@/lib/money'
import { useStockMovements } from './queries'

/**
 * A product's ledger.
 *
 * Append-only (CLAUDE.md invariant 4), so nothing here is editable and nothing
 * here changes retroactively. A correction is a new row, not an edit — which is
 * what makes the running total reconstructable.
 */
export function StockMovementsPanel({
  productId,
  productName,
  onClose,
}: {
  productId: string
  productName: string
  onClose: () => void
}) {
  const movements = useStockMovements(productId)
  const rows = movements.data?.pages.flatMap((page) => page.items) ?? []

  return (
    <div
      role="dialog"
      aria-modal="true"
      aria-labelledby="ledger-title"
      className="fixed inset-0 z-40 flex items-center justify-center bg-background/80 p-4 backdrop-blur-sm"
    >
      <div className="flex max-h-[80vh] w-full max-w-2xl flex-col rounded-xl border border-border bg-card shadow-lg">
        <header className="flex items-start justify-between gap-4 border-b border-border p-6">
          <div>
            <h2 id="ledger-title" className="text-lg font-semibold text-card-foreground">
              Stock ledger
            </h2>
            <p className="mt-0.5 text-sm text-muted-foreground">{productName}</p>
          </div>
          <Button variant="outline" onClick={onClose}>
            Close
          </Button>
        </header>

        <div className="min-h-0 flex-1 overflow-y-auto p-6">
          {movements.isPending ? (
            <LoadingState label="Loading the ledger…" />
          ) : movements.isError ? (
            <ErrorState
              error={movements.error}
              onRetry={() => {
                void movements.refetch()
              }}
              title="Could not load the ledger."
            />
          ) : rows.length === 0 ? (
            <EmptyState
              title="No movements yet."
              description="Nothing has been received, sold or adjusted for this product."
            />
          ) : (
            <>
              <table className="w-full text-sm">
                <thead className="text-left text-xs text-muted-foreground">
                  <tr>
                    <th className="pb-2 font-medium">When</th>
                    <th className="pb-2 font-medium">Type</th>
                    <th className="pb-2 text-right font-medium">Change</th>
                    <th className="pb-2 font-medium">Reason</th>
                  </tr>
                </thead>
                <tbody>
                  {rows.map((movement) => (
                    <tr key={movement.id} className="border-t border-border">
                      <td className="py-2 whitespace-nowrap text-muted-foreground">
                        {/* Stored and sent as UTC; rendered in the reader's
                            own zone. The trading-day boundary that matters for
                            Z-reports is a Phase 6 concern. */}
                        {new Date(movement.occurredAt).toLocaleString()}
                      </td>
                      <td className="py-2">{movement.type}</td>
                      <td className="py-2 text-right tabular-nums">
                        {Number(movement.quantity) > 0 ? '+' : ''}
                        {formatQuantity(movement.quantity)}
                      </td>
                      <td className="py-2 text-muted-foreground">
                        {movement.reason ?? (movement.saleId === null ? '—' : 'From a sale')}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>

              {movements.hasNextPage ? (
                <Button
                  variant="outline"
                  className="mt-4 w-full"
                  disabled={movements.isFetchingNextPage}
                  onClick={() => {
                    void movements.fetchNextPage()
                  }}
                >
                  {movements.isFetchingNextPage ? 'Loading…' : 'Older movements'}
                </Button>
              ) : null}
            </>
          )}
        </div>
      </div>
    </div>
  )
}
