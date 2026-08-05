import { useEffect, useRef } from 'react'
import { Button } from '@/components/ui/button'
import { ConfirmButton } from '@/components/ConfirmButton'
import { EmptyState } from '@/components/states'
import { formatMinorUnits, formatMoney, formatQuantity, type ServerDecimal } from '@/lib/money'
import { cn } from '@/lib/utils'
import { provisionalLineMinor, type Cart, type CartLine } from './cart'
import { useCart } from './cartContext'
import type { Adjustment } from './LineAdjustDialog'

/**
 * The cart: what the customer is buying, and the thing the cashier looks at.
 *
 * Line totals come from the server's quote wherever it has one — the client
 * never computes a price a customer pays (CLAUDE.md invariant 3). Until the
 * quote lands, the provisional integer minor-unit figure is shown in muted type,
 * so the screen is never blank between a scan and the round trip.
 *
 * The discount and price controls appear on the **selected** line only. On a row
 * that already carries a quantity stepper and a void, four more buttons per line
 * would shrink every target below a finger; and the keyboard path (`F3`/`F4`)
 * acts on the selection anyway, so the mouse and the keyboard agree about what
 * "this line" means.
 */
export function CartPane({
  currency,
  pricedLines,
  isQuoting,
  onAdjust,
}: {
  currency: string
  /** `productId` → line total, from `POST /sales/quote`. */
  pricedLines: ReadonlyMap<string, ServerDecimal>
  isQuoting: boolean
  onAdjust: (adjustment: Adjustment, line: CartLine | null) => void
}) {
  const { cart, dispatch } = useCart()

  return (
    <section
      aria-label="Cart"
      className="flex min-h-0 flex-col rounded-xl border border-border bg-card"
    >
      <header className="flex items-center justify-between gap-3 border-b border-border px-4 py-2.5">
        <h2 className="text-sm font-semibold text-card-foreground">Cart</h2>

        {cart.lines.length > 0 ? (
          <div className="flex items-center gap-2">
            <Button
              variant="outline"
              size="sm"
              onClick={() => {
                onAdjust('cartDiscount', null)
              }}
            >
              {cart.cartDiscountAmount === null ? 'Discount sale' : 'Change sale discount'}
            </Button>

            <ConfirmButton
              confirmLabel="Void the whole cart?"
              onConfirm={() => {
                dispatch({ type: 'clear' })
              }}
            >
              Void cart
            </ConfirmButton>
          </div>
        ) : null}
      </header>

      {cart.cartDiscountAmount !== null ? (
        <p
          data-testid="cart-discount"
          className="border-b border-border bg-muted/40 px-4 py-1.5 text-xs text-muted-foreground"
        >
          <span className="font-medium text-foreground">
            {formatMoney(cart.cartDiscountAmount, currency)} off the sale
          </span>{' '}
          — the server spreads it across the lines.
        </p>
      ) : null}

      {cart.lines.length === 0 ? (
        <div className="flex flex-1 items-center justify-center p-6">
          <EmptyState
            className="w-full border-none"
            title="Scan an item to start."
            description="Or pick one from the grid. Nothing has been charged yet."
          />
        </div>
      ) : (
        <ul className="min-h-0 flex-1 divide-y divide-border overflow-y-auto">
          {cart.lines.map((line) => (
            <CartRow
              key={line.key}
              line={line}
              cart={cart}
              currency={currency}
              priced={pricedLines.get(line.productId)}
              isQuoting={isQuoting}
              onSelect={() => {
                dispatch({ type: 'select', key: line.key })
              }}
              onStep={(delta) => {
                dispatch({ type: 'adjustQuantity', key: line.key, delta })
              }}
              onRemove={() => {
                dispatch({ type: 'remove', key: line.key })
              }}
              onAdjust={onAdjust}
            />
          ))}
        </ul>
      )}
    </section>
  )
}

function CartRow({
  line,
  cart,
  currency,
  priced,
  isQuoting,
  onSelect,
  onStep,
  onRemove,
  onAdjust,
}: {
  line: CartLine
  cart: Cart
  currency: string
  priced: ServerDecimal | undefined
  isQuoting: boolean
  onSelect: () => void
  onStep: (delta: number) => void
  onRemove: () => void
  onAdjust: (adjustment: Adjustment, line: CartLine) => void
}) {
  const isSelected = cart.selectedKey === line.key
  const ref = useRef<HTMLLIElement>(null)

  // Keyboard selection has to stay on screen, or ↓ walks the cursor into a part
  // of a long cart nobody can see.
  useEffect(() => {
    if (isSelected) {
      ref.current?.scrollIntoView({ block: 'nearest' })
    }
  }, [isSelected])

  return (
    <li
      ref={ref}
      data-testid="cart-line"
      data-selected={isSelected}
      className={cn(
        'flex flex-col transition-colors',
        isSelected && 'bg-muted',
        // The scan flash. Feedback matters more than subtlety here: staff are
        // looking at the goods, and a glance has to answer "did that register?".
        cart.flashedKey === line.key && 'animate-pulse bg-primary/10',
      )}
    >
      <div className="flex items-center gap-3 px-4 py-2.5">
        <button
          type="button"
          onClick={onSelect}
          className="min-w-0 flex-1 text-left"
          aria-label={`Select ${line.description}`}
        >
          <span className="block truncate text-sm font-medium text-card-foreground">
            {line.description}
          </span>
          <span className="block truncate text-xs text-muted-foreground">
            {formatQuantity(line.quantity)}
            {line.unit === 'Each' ? '' : line.unit === 'Kilogram' ? ' kg' : ' L'} ×{' '}
            {line.unitPriceOverride === null ? (
              formatMoney(line.unitPrice, currency)
            ) : (
              // Both prices, because "why is this £2 cheaper" is asked at the
              // counter and the answer has to be on the screen being asked about.
              <>
                <span className="line-through">{formatMoney(line.unitPrice, currency)}</span>{' '}
                <span data-testid="price-override" className="font-medium text-foreground">
                  {formatMoney(line.unitPriceOverride, currency)}
                </span>
              </>
            )}
            {line.discountAmount !== null ? (
              <span data-testid="line-discount" className="font-medium text-foreground">
                {' '}
                · −{formatMoney(line.discountAmount, currency)}
              </span>
            ) : null}
          </span>
        </button>

        <div className="flex items-center gap-1">
          <Button
            variant="outline"
            size="icon-lg"
            aria-label={`Less ${line.description}`}
            onClick={() => {
              onStep(-1)
            }}
          >
            −
          </Button>
          <Button
            variant="outline"
            size="icon-lg"
            aria-label={`More ${line.description}`}
            onClick={() => {
              onStep(1)
            }}
          >
            +
          </Button>
        </div>

        <span
          data-testid="cart-line-total"
          className={cn(
            'w-24 text-right text-sm font-semibold tabular-nums',
            priced === undefined && 'text-muted-foreground italic',
          )}
          // The provisional figure is not the price. Saying so out loud is
          // cheaper than a cashier reading a stale number to a customer.
          title={priced === undefined ? 'Waiting for the server to price this line' : undefined}
        >
          {priced === undefined
            ? formatMinorUnits(provisionalLineMinor(line), currency)
            : formatMoney(priced, currency)}
          {isQuoting ? <span className="sr-only"> (updating)</span> : null}
        </span>

        <Button
          variant="ghost"
          size="icon-lg"
          aria-label={`Void ${line.description}`}
          onClick={onRemove}
        >
          ×
        </Button>
      </div>

      {isSelected ? (
        <div className="flex flex-wrap items-center gap-2 px-4 pb-2.5">
          <Button
            variant="outline"
            size="sm"
            onClick={() => {
              onAdjust('lineDiscount', line)
            }}
          >
            {line.discountAmount === null ? 'Discount' : 'Change discount'}
          </Button>
          <Button
            variant="outline"
            size="sm"
            onClick={() => {
              onAdjust('priceOverride', line)
            }}
          >
            {line.unitPriceOverride === null ? 'Change price' : 'Change price again'}
          </Button>
          <span className="text-xs text-muted-foreground">F3 · F4</span>
        </div>
      ) : null}
    </li>
  )
}
