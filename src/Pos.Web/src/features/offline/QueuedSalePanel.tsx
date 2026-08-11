/**
 * A sale taken while the till could not reach the server.
 *
 * A separate component from `SaleCompletePanel` rather than a flag on it,
 * because almost everything it says is different — and the differences are the
 * ones a cashier has to understand:
 *
 * - **There is no sale number.** `SaleSequence` is a per-tenant counter assigned
 *   inside the server's transaction, so nothing offline can know it. The panel
 *   shows a local reference instead and says where the number will come from,
 *   rather than printing a blank where staff expect one.
 * - **It says the sale is saved here, not sent.** Overstating this is the most
 *   damaging thing this phase can do: a shop that believes a queued sale is
 *   safely at the server, and then clears its browser data, loses a day's
 *   takings and will not come back.
 *
 * The change due is still the largest thing on screen and is still computed
 * once, by the pricing engine, from the same rules the server uses.
 */

import { Button } from '@/components/ui/button'
import type { OutboxDisplay } from '@/lib/offline/db'

export function QueuedSalePanel({
  saleKey,
  display,
  onPrint,
}: {
  saleKey: string
  display: OutboxDisplay
  onPrint?: () => void
}) {
  const change = Number(display.change)

  return (
    <section
      aria-label="Sale saved on this till"
      data-testid="sale-queued"
      className="flex flex-col gap-3 rounded-xl border border-amber-500/40 bg-amber-500/5 p-4"
    >
      <div className="flex items-baseline justify-between gap-3">
        <span className="text-sm font-semibold text-foreground">
          {change > 0 ? 'Change due' : 'Paid in full'}
        </span>
        {/* The first eight characters of the sale's own GUID. Enough for a
            person to match this receipt to a queued sale on the review screen,
            and not pretending to be the number a receipt normally carries. */}
        <span className="text-xs text-muted-foreground">Ref {saleKey.slice(0, 8)}</span>
      </div>

      <p
        data-testid="change-due"
        className="text-6xl leading-none font-semibold tabular-nums text-foreground"
      >
        {money(display.change, display.currencyCode)}
      </p>

      <p
        role="status"
        data-testid="sale-queued-notice"
        className="rounded-md bg-amber-500/15 px-3 py-2 text-sm text-foreground"
      >
        <span className="font-medium">Saved on this till.</span> The server could not be reached, so
        this sale is waiting here and will be sent automatically. It has no sale number until then.
      </p>

      <dl className="grid grid-cols-2 gap-x-3 gap-y-1 text-sm">
        <Row label="Total" value={money(display.total, display.currencyCode)} />
        <Row label="Cash taken" value={money(display.tendered, display.currencyCode)} />
        {display.roundingAdjustment !== '0.00' && display.roundingAdjustment !== '0' ? (
          <Row
            label="Cash rounding"
            value={money(display.roundingAdjustment, display.currencyCode)}
          />
        ) : null}
      </dl>

      {onPrint === undefined ? null : (
        <Button variant="outline" size="sm" className="self-start" onClick={onPrint}>
          Print receipt
        </Button>
      )}

      <p className="text-xs text-muted-foreground">Scan the next item to start a new sale.</p>
    </section>
  )
}

/**
 * Formats an already-computed amount.
 *
 * The string arrives from the pricing engine at the payable scale and is only
 * being *displayed* — this is the one place a money value becomes a `number`,
 * and it does so after every decision has been made. Nothing downstream
 * calculates with it.
 */
function money(amount: string, currency: string): string {
  return new Intl.NumberFormat(undefined, { style: 'currency', currency }).format(Number(amount))
}

function Row({ label, value }: { label: string; value: string }) {
  return (
    <>
      <dt className="text-muted-foreground">{label}</dt>
      <dd className="text-right tabular-nums text-foreground">{value}</dd>
    </>
  )
}
