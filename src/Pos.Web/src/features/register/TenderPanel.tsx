import { Button } from '@/components/ui/button'
import { formatMinorUnits, formatMoney, parseServerDecimal, toMinorUnits } from '@/lib/money'
import { cn } from '@/lib/utils'
import type { components } from '@/api/schema'
import {
  isCovered,
  provisionalChangeMinor,
  quickCash,
  remainingMinor,
  tenderedMinor,
  type Tender,
} from './tender'

type SaleResponse = components['schemas']['SaleResponse']

/**
 * Taking the money.
 *
 * Replaces the total panel in the right-hand column rather than covering the
 * screen, so the cart stays visible: the moment a customer is most likely to say
 * "actually, take that off" is while they are reaching for their wallet, and a
 * tender screen that hid the cart would make the cashier back out to do it.
 *
 * **Every figure here except the total is provisional and says so.** The total
 * is the server's from `POST /sales/quote`; the change a customer is owed comes
 * back from `POST /sales` and is shown by `SaleCompletePanel`. What this panel
 * computes is a running balance in integer minor units, to keep the cashier
 * oriented while they count — see `tender.ts`.
 */
export function TenderPanel({
  quote,
  currency,
  tenders,
  pending,
  submitting,
  onAdd,
  onRemove,
  onComplete,
  onCancel,
}: {
  quote: SaleResponse
  currency: string
  tenders: readonly Tender[]
  /** The keypad buffer, as typed. */
  pending: string
  submitting: boolean
  onAdd: (amountMinor: number) => void
  onRemove: (key: string) => void
  onComplete: () => void
  onCancel: () => void
}) {
  const totalMinor = toMinorUnits(quote.total)
  const remaining = remainingMinor(totalMinor, tenders)
  const change = provisionalChangeMinor(totalMinor, tenders)
  const covered = isCovered(totalMinor, tenders)
  const rounding = parseServerDecimal(quote.roundingAdjustment)

  return (
    <section
      aria-label="Take payment"
      data-testid="tender-panel"
      className="flex flex-col gap-3 rounded-xl border border-border bg-card p-4"
    >
      <div className="flex items-baseline justify-between gap-3">
        <span className="text-sm font-medium text-muted-foreground">Cash due</span>
        <span className="text-xs text-muted-foreground">Priced by the server</span>
      </div>

      <p
        data-testid="tender-due"
        className="text-4xl leading-none font-semibold tabular-nums text-card-foreground"
      >
        {formatMoney(quote.total, currency)}
      </p>

      {rounding !== 0 ? (
        <p data-testid="cash-rounding" className="text-xs text-muted-foreground">
          {/* Explained rather than merely shown: a total that is not the sum of
              the lines looks like a bug to whoever has to defend it at a
              counter. */}
          Includes {formatMoney(quote.roundingAdjustment, currency)} cash rounding — the shop rounds
          cash payments to the nearest coin it holds.
        </p>
      ) : null}

      {/* One press for the amounts a person actually hands over. These are
          suggestions, not prices: the customer's change is still the server's
          arithmetic. */}
      <div className="grid grid-cols-2 gap-2">
        {quickCash(totalMinor, currency).map((amountMinor, index) => (
          <Button
            key={amountMinor}
            variant="outline"
            size="lg"
            className="h-12 text-base tabular-nums"
            disabled={submitting}
            onClick={() => {
              onAdd(amountMinor)
            }}
          >
            {index === 0 ? 'Exact' : formatMinorUnits(amountMinor, currency)}
          </Button>
        ))}
      </div>

      <div
        data-testid="tender-buffer"
        aria-live="polite"
        className="rounded-lg border border-dashed border-border px-3 py-2 text-right font-mono text-sm text-foreground"
      >
        {pending === '' ? (
          <span className="text-muted-foreground">Type an amount, then Enter</span>
        ) : (
          pending
        )}
      </div>

      {tenders.length > 0 ? (
        <ul className="flex flex-col gap-1" aria-label="Cash taken">
          {tenders.map((tender) => (
            <li
              key={tender.key}
              data-testid="tender-line"
              className="flex items-center justify-between gap-2 rounded-md bg-muted px-3 py-1.5 text-sm"
            >
              <span className="text-muted-foreground">Cash</span>
              <span className="flex-1 text-right font-medium tabular-nums text-foreground">
                {formatMinorUnits(tender.amountMinor, currency)}
              </span>
              <Button
                variant="ghost"
                size="sm"
                aria-label={`Remove ${formatMinorUnits(tender.amountMinor, currency)}`}
                disabled={submitting}
                onClick={() => {
                  onRemove(tender.key)
                }}
              >
                ×
              </Button>
            </li>
          ))}
        </ul>
      ) : null}

      {/* The split-tender running balance. Provisional, and labelled — the
          authoritative change is on the sale's response. */}
      <dl className="grid grid-cols-2 gap-x-3 gap-y-1 text-sm">
        <dt className="text-muted-foreground">Taken</dt>
        <dd className="text-right tabular-nums text-foreground">
          {formatMinorUnits(tenderedMinor(tenders), currency)}
        </dd>

        <dt className={cn('font-medium', covered ? 'text-muted-foreground' : 'text-foreground')}>
          {covered ? 'Change (provisional)' : 'Still owed'}
        </dt>
        <dd
          data-testid="tender-remaining"
          className={cn(
            'text-right font-semibold tabular-nums',
            covered ? 'text-muted-foreground' : 'text-foreground',
          )}
        >
          {formatMinorUnits(covered ? change : remaining, currency)}
        </dd>
      </dl>

      <div className="flex flex-col gap-2">
        <Button
          size="lg"
          data-testid="complete-sale"
          className="h-14 text-base"
          // A courtesy, not the mechanism: the server refuses an under-tender
          // with 409 .../under-tender whatever this button is doing, and the
          // idempotency key is what stops a double press becoming two sales.
          disabled={!covered || submitting}
          onClick={onComplete}
        >
          {submitting ? 'Completing…' : 'Complete sale'}
        </Button>

        <Button variant="ghost" disabled={submitting} onClick={onCancel}>
          Back to the cart — Esc
        </Button>
      </div>
    </section>
  )
}
