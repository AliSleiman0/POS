import { useState, type FormEvent } from 'react'
import { Button } from '@/components/ui/button'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { isStorableAmount } from '@/features/catalog/validation'
import { formatMinorUnits, formatMoney } from '@/lib/money'
import { effectiveUnitPriceMinor, provisionalLineMinor, type CartLine } from './cart'

/** What the dialog is being opened to change. */
export type Adjustment = 'lineDiscount' | 'priceOverride' | 'cartDiscount'

/**
 * Entry for the one amount a person types.
 *
 * **Absolute amounts only** — the API has no percentage discount, and computing
 * "10% off" in TypeScript would put client-side arithmetic on the exact field
 * that decides what a customer pays (CLAUDE.md invariant 3). What is typed here
 * is carried to the server verbatim; the server prices the result.
 *
 * In-page rather than `prompt()`, which is forbidden outright in the register
 * (invariant 10): it blocks the event loop, so a scan arriving behind it queues
 * and replays into whatever has focus afterwards.
 */
export function LineAdjustDialog({
  adjustment,
  line,
  currency,
  cartSubtotalMinor,
  authorizedByName,
  onApply,
  onCancel,
}: {
  adjustment: Adjustment
  /** The line being changed. Absent for a cart discount. */
  line: CartLine | null
  currency: string
  /** Provisional cart subtotal, for the cart discount's ceiling. */
  cartSubtotalMinor: number
  /** Shown when a manager authorised this rather than the cashier. */
  authorizedByName: string | null
  onApply: (amount: number | null) => void
  onCancel: () => void
}) {
  const existing = currentValue(adjustment, line)

  const [value, setValue] = useState(existing === null ? '' : String(existing))
  const [error, setError] = useState<string | null>(null)

  const ceilingMinor =
    adjustment === 'cartDiscount'
      ? cartSubtotalMinor
      : line === null
        ? 0
        : provisionalLineMinor(line)

  function onSubmit(event: FormEvent) {
    event.preventDefault()

    const trimmed = value.trim()

    if (trimmed === '') {
      // An empty box is "take it off again", which is the fastest way to undo a
      // mistake and needs no separate control.
      onApply(null)
      return
    }

    if (!isStorableAmount(trimmed)) {
      setError('An amount of 0 or more with at most 4 decimal places is required.')
      return
    }

    const amount = Number(trimmed)

    /*
     * A discount larger than what it applies to is refused here as well as by
     * the server, which throws `InvalidDiscountException` for it. Catching it at
     * the keypad turns a 400 nobody can read at a counter into a sentence that
     * says what is wrong — and the server still refuses it regardless, so this
     * is a courtesy rather than the rule.
     */
    if (adjustment !== 'priceOverride' && Math.round(amount * 100) > ceilingMinor) {
      setError(
        `That is more than the ${adjustment === 'cartDiscount' ? 'cart' : 'line'} comes to (` +
          `${formatMinorUnits(ceilingMinor, currency)}).`,
      )
      return
    }

    onApply(amount)
  }

  return (
    <div
      role="dialog"
      aria-modal="true"
      aria-labelledby="line-adjust-title"
      data-testid="line-adjust"
      className="fixed inset-0 z-40 flex items-center justify-center bg-background/80 p-4 backdrop-blur-sm"
    >
      <form
        onSubmit={onSubmit}
        className="w-full max-w-sm rounded-xl border border-border bg-card p-6 shadow-lg"
      >
        <h2 id="line-adjust-title" className="text-lg font-semibold text-card-foreground">
          {title(adjustment)}
        </h2>

        <p className="mt-1 text-sm text-muted-foreground">
          {adjustment === 'cartDiscount' ? (
            <>The whole sale, currently {formatMinorUnits(cartSubtotalMinor, currency)}.</>
          ) : line === null ? null : (
            <>
              {line.description}, normally {formatMoney(line.unitPrice, currency)} each
              {line.unitPriceOverride === null
                ? ''
                : ` — currently ${formatMinorUnits(effectiveUnitPriceMinor(line), currency)}`}
              .
            </>
          )}
        </p>

        {authorizedByName !== null ? (
          <p data-testid="authorized-by" className="mt-2 text-sm font-medium text-foreground">
            Authorised by {authorizedByName}.
          </p>
        ) : null}

        <div className="mt-5">
          <Field label={label(adjustment, currency)} error={error ?? undefined}>
            {(fieldProps) => (
              <Input
                {...fieldProps}
                // `inputMode` rather than type="number": a numeric input gets
                // spinner arrows and accepts `e` and `-`.
                inputMode="decimal"
                autoComplete="off"
                autoFocus
                data-testid="adjust-amount"
                className="h-14 text-right text-2xl tabular-nums"
                value={value}
                onChange={(event) => {
                  setValue(event.target.value)
                  setError(null)
                }}
              />
            )}
          </Field>

          <p className="mt-2 text-xs text-muted-foreground">
            {existing === null
              ? 'The server prices the result — this is not the total.'
              : 'Clear the box and apply to remove it.'}
          </p>
        </div>

        <div className="mt-6 flex justify-end gap-2">
          <Button type="button" variant="ghost" onClick={onCancel}>
            Cancel
          </Button>
          <Button type="submit">Apply</Button>
        </div>
      </form>
    </div>
  )
}

function currentValue(adjustment: Adjustment, line: CartLine | null): number | null {
  if (adjustment === 'priceOverride') {
    return line?.unitPriceOverride ?? null
  }

  return adjustment === 'lineDiscount' ? (line?.discountAmount ?? null) : null
}

function title(adjustment: Adjustment): string {
  switch (adjustment) {
    case 'lineDiscount':
      return 'Discount this line'
    case 'priceOverride':
      return 'Change this price'
    case 'cartDiscount':
      return 'Discount the sale'
  }
}

function label(adjustment: Adjustment, currency: string): string {
  return adjustment === 'priceOverride'
    ? `New unit price (${currency})`
    : `Amount off (${currency})`
}
