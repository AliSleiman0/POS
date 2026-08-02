import type { components } from '@/api/schema'
import { Button } from '@/components/ui/button'
import { formatMinorUnits, formatMoney, parseServerDecimal } from '@/lib/money'
import { cn } from '@/lib/utils'
import type { Cart } from './cart'

type SaleResponse = components['schemas']['SaleResponse']

/**
 * The number read across a counter, and the keypad next to it.
 *
 * **The total is the server's.** `POST /sales/quote` prices the same cart the
 * sale will be built from, through the same function, so the figure here and the
 * figure on the receipt cannot disagree. Before the first quote returns, the
 * integer minor-unit subtotal stands in — visibly, in muted type, labelled as
 * provisional, because a till that shows an authoritative-looking number it made
 * up itself is worse than one that shows nothing.
 */
export function TotalPanel({
  cart,
  quote,
  currency,
  provisionalMinor,
  isQuoting,
  quoteFailed,
  pending,
}: {
  cart: Cart
  quote: SaleResponse | undefined
  currency: string
  provisionalMinor: number
  isQuoting: boolean
  quoteFailed: boolean
  /** The keypad buffer, as typed. */
  pending: string
}) {
  // Lines, not summed quantity: a cart of two waters and 0.35 kg of cheese is
  // not "3.35 items", and a weighed line has no unit count worth adding up.
  const lines = cart.lines.length
  const rounding = quote === undefined ? 0 : parseServerDecimal(quote.roundingAdjustment)

  return (
    <section
      aria-label="Total"
      className="flex flex-col gap-3 rounded-xl border border-border bg-card p-4"
    >
      <div className="flex items-baseline justify-between gap-3">
        <span className="text-sm font-medium text-muted-foreground">
          {lines === 0 ? 'Nothing in the cart' : `${String(lines)} line${lines === 1 ? '' : 's'}`}
        </span>
        {isQuoting ? (
          <span className="text-xs text-muted-foreground">Pricing…</span>
        ) : quote === undefined && !quoteFailed ? null : quoteFailed ? (
          <span className="text-xs font-medium text-destructive">Price unconfirmed</span>
        ) : (
          <span className="text-xs text-muted-foreground">Priced by the server</span>
        )}
      </div>

      <p
        data-testid="cart-total"
        className={cn(
          // Big enough to read from a metre away, which is the actual
          // requirement: the customer reads this as often as the cashier does.
          'text-5xl leading-none font-semibold tabular-nums text-card-foreground',
          quote === undefined && 'text-muted-foreground italic',
        )}
      >
        {quote === undefined
          ? formatMinorUnits(provisionalMinor, currency)
          : formatMoney(quote.total, currency)}
      </p>

      {quote === undefined && cart.lines.length > 0 ? (
        <p className="text-xs text-muted-foreground">
          Provisional — waiting for the server to price this cart.
        </p>
      ) : null}

      {quote !== undefined ? (
        <dl className="grid grid-cols-2 gap-x-3 gap-y-1 text-xs text-muted-foreground">
          <dt>Subtotal</dt>
          <dd className="text-right tabular-nums">{formatMoney(quote.subtotal, currency)}</dd>
          <dt>Tax</dt>
          <dd className="text-right tabular-nums">{formatMoney(quote.taxTotal, currency)}</dd>
          {rounding !== 0 ? (
            <>
              {/* Shown whenever it applies, so a total that is not the sum of
                  the lines is explained rather than looking like a bug. */}
              <dt>Cash rounding</dt>
              <dd className="text-right tabular-nums">
                {formatMoney(quote.roundingAdjustment, currency)}
              </dd>
            </>
          ) : null}
        </dl>
      ) : null}

      <div
        data-testid="keypad-buffer"
        aria-live="polite"
        className="rounded-lg border border-dashed border-border px-3 py-2 text-right font-mono text-sm text-foreground"
      >
        {pending === '' ? (
          <span className="text-muted-foreground">Type a quantity, then Enter</span>
        ) : (
          pending
        )}
      </div>

      {/* Present and honest: the tender flow is 5.4, and a button that pretended
          to take money would be worse than one that says it cannot yet. */}
      <div className="flex flex-col gap-1">
        <Button size="lg" disabled className="h-12 text-base">
          Take cash
        </Button>
        <p className="text-center text-xs text-muted-foreground">
          Taking payment arrives in the next milestone.
        </p>
      </div>
    </section>
  )
}
