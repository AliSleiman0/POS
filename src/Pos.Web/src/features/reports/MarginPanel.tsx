import { ErrorState, LoadingState } from '@/components/states'
import { formatMoney, formatQuantity } from '@/lib/money'
import { useMargins } from './queries'

/**
 * What the day's sales earned over what they cost. <b>Owner-only.</b>
 *
 * The server refuses this route to anyone without `CanViewMargins` — a cost
 * price is the owner's commercial position, and a manager who can read it can
 * price-shop the shop's suppliers. This component is only rendered behind the
 * same policy so that an owner-less screen shows nothing rather than a panel
 * that answers 403.
 *
 * **Cost is not snapshotted**, unlike revenue. `SaleLine` records no cost price,
 * so the figures move when a supplier's price changes. That is stated on the
 * panel rather than left for someone to discover from a number that shifted.
 */
export function MarginPanel({ date }: { date: string | null }) {
  const margins = useMargins(date, date, true)

  return (
    <section data-testid="report-margins" className="rounded-xl border border-border p-4">
      <h2 className="text-sm font-semibold text-foreground">Margin</h2>
      <p className="mb-3 text-xs text-muted-foreground">
        Revenue is what was charged at the time. Cost is today&rsquo;s cost price, so these move
        when a supplier&rsquo;s does — sale lines do not record a cost.
      </p>

      {margins.isPending ? (
        <LoadingState label="Working out the margin…" />
      ) : margins.isError ? (
        <ErrorState
          error={margins.error}
          title="The margin report could not be produced."
          onRetry={() => {
            void margins.refetch()
          }}
        />
      ) : margins.data.lines.length === 0 ? (
        <p className="text-sm text-muted-foreground">Nothing was sold.</p>
      ) : (
        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead>
              <tr>
                <th className="pb-1 text-left font-medium text-muted-foreground">Product</th>
                <th className="pb-1 text-right font-medium text-muted-foreground">Sold</th>
                <th className="pb-1 text-right font-medium text-muted-foreground">Revenue</th>
                <th className="pb-1 text-right font-medium text-muted-foreground">Cost</th>
                <th className="pb-1 text-right font-medium text-muted-foreground">Margin</th>
              </tr>
            </thead>
            <tbody>
              {margins.data.lines.map((line) => (
                <tr key={line.productId} className="border-t border-border">
                  <td className="py-1 text-foreground">{line.description}</td>
                  <td className="py-1 text-right tabular-nums">{formatQuantity(line.quantity)}</td>
                  <td className="py-1 text-right tabular-nums">
                    {formatMoney(line.revenue, margins.data.currencyCode)}
                  </td>
                  {/* Unknown, not zero. A blank cost shown as 0 reads as a 100%
                      margin, which is the direction that gets a shop into
                      trouble. */}
                  <td className="py-1 text-right tabular-nums">
                    {line.cost === null
                      ? 'Unknown'
                      : formatMoney(line.cost, margins.data.currencyCode)}
                  </td>
                  <td className="py-1 text-right tabular-nums">
                    {line.margin === null
                      ? '—'
                      : formatMoney(line.margin, margins.data.currencyCode)}
                  </td>
                </tr>
              ))}
              <tr className="border-t-2 border-border font-semibold">
                <td className="py-1 text-foreground">Total</td>
                <td />
                <td className="py-1 text-right tabular-nums">
                  {formatMoney(margins.data.revenue, margins.data.currencyCode)}
                </td>
                <td className="py-1 text-right tabular-nums">
                  {margins.data.cost === null
                    ? 'Unknown'
                    : formatMoney(margins.data.cost, margins.data.currencyCode)}
                </td>
                <td className="py-1 text-right tabular-nums" data-testid="margin-total">
                  {margins.data.margin === null
                    ? '—'
                    : formatMoney(margins.data.margin, margins.data.currencyCode)}
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      )}
    </section>
  )
}
