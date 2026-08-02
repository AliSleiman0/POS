import { useEffect, useRef } from 'react'
import { Button } from '@/components/ui/button'
import { ConfirmButton } from '@/components/ConfirmButton'
import { EmptyState } from '@/components/states'
import { formatMinorUnits, formatMoney, formatQuantity, type ServerDecimal } from '@/lib/money'
import { cn } from '@/lib/utils'
import { provisionalLineMinor, type Cart, type CartLine } from './cart'
import { useCart } from './cartContext'

/**
 * The cart: what the customer is buying, and the thing the cashier looks at.
 *
 * Line totals come from the server's quote wherever it has one — the client
 * never computes a price a customer pays (CLAUDE.md invariant 3). Until the
 * quote lands, the provisional integer minor-unit figure is shown in muted type,
 * so the screen is never blank between a scan and the round trip.
 */
export function CartPane({
  currency,
  pricedLines,
  isQuoting,
}: {
  currency: string
  /** `productId` → line total, from `POST /sales/quote`. */
  pricedLines: ReadonlyMap<string, ServerDecimal>
  isQuoting: boolean
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
          <ConfirmButton
            confirmLabel="Void the whole cart?"
            onConfirm={() => {
              dispatch({ type: 'clear' })
            }}
          >
            Void cart
          </ConfirmButton>
        ) : null}
      </header>

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
}: {
  line: CartLine
  cart: Cart
  currency: string
  priced: ServerDecimal | undefined
  isQuoting: boolean
  onSelect: () => void
  onStep: (delta: number) => void
  onRemove: () => void
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
        'flex items-center gap-3 px-4 py-2.5 transition-colors',
        isSelected && 'bg-muted',
        // The scan flash. Feedback matters more than subtlety here: staff are
        // looking at the goods, and a glance has to answer "did that register?".
        cart.flashedKey === line.key && 'animate-pulse bg-primary/10',
      )}
    >
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
          {formatMoney(line.unitPrice, currency)}
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
    </li>
  )
}
