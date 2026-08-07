import { useMemo, useState } from 'react'
import { Button } from '@/components/ui/button'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { useToast } from '@/components/toastContext'
import { useCurrentShift } from '@/features/register/queries'
import { formatMoney, formatQuantity } from '@/lib/money'
import { useRefundSale, type Sale } from './queries'

/**
 * Returning all or part of a sale.
 *
 * **In-page, never `confirm()`** (CLAUDE.md invariant 10) — and this one is
 * reached from the counter, where a blocked tab means a queue.
 *
 * The refund goes onto the drawer that is **open now**, not the one the original
 * sale was rung on: the cash comes out of the till the customer is standing at,
 * which is where it physically is. So this needs an open shift, and says so when
 * there is not one rather than failing at the server.
 */
export function RefundDialog({
  sale,
  currency,
  onClose,
  onRefunded,
}: {
  sale: Sale
  currency: string
  onClose: () => void
  onRefunded: (refundId: string) => void
}) {
  const toast = useToast()
  const shift = useCurrentShift()

  /*
   * Minted once, when the dialog opens, and reused on every attempt — CLAUDE.md
   * invariant 6. A key generated per press would make the header decorative, and
   * a timeout followed by a second press would pay the customer twice.
   *
   * `useMemo` with no dependencies: the identity belongs to this dialog, so it
   * survives every re-render and dies when the dialog closes.
   */
  const idempotencyKey = useMemo(() => crypto.randomUUID(), [])
  const refund = useRefundSale(idempotencyKey)

  const [reason, setReason] = useState('')
  const [whole, setWhole] = useState(true)

  /** Quantity being returned per line, keyed by sale line id. */
  const [quantities, setQuantities] = useState<Record<string, string>>({})

  const lines = sale.lines.map((line) => ({
    ...line,
    returning: quantities[line.id] ?? '',
  }))

  const partial = lines
    .filter((line) => line.returning !== '' && Number(line.returning) > 0)
    .map((line) => ({ saleLineId: line.id, quantity: Number(line.returning) }))

  const canSubmit =
    reason.trim() !== '' &&
    shift.data !== undefined &&
    shift.registerId !== null &&
    (whole || partial.length > 0) &&
    !refund.isPending

  return (
    <div
      role="dialog"
      aria-modal="true"
      aria-label="Refund this sale"
      data-testid="refund-dialog"
      className="fixed inset-0 z-50 flex items-start justify-center overflow-y-auto bg-black/50 p-6"
    >
      <div className="flex w-full max-w-lg flex-col gap-4 rounded-xl border border-border bg-background p-5">
        <div>
          <h2 className="text-base font-semibold text-foreground">
            Refund sale #{String(sale.saleNumber)}
          </h2>
          <p className="text-sm text-muted-foreground">
            The money comes out of the drawer that is open now, not the one this was sold on.
          </p>
        </div>

        {shift.data === undefined ? (
          <p role="alert" className="rounded-md border border-destructive/40 bg-destructive/10 px-3 py-2 text-sm">
            <span className="font-medium">No drawer is open.</span> Open one on the register
            before refunding — the cash has to come from somewhere.
          </p>
        ) : null}

        <fieldset className="flex flex-col gap-2">
          <legend className="sr-only">How much to refund</legend>

          <label className="flex items-center gap-2 text-sm text-foreground">
            <input
              type="radio"
              name="scope"
              checked={whole}
              onChange={() => {
                setWhole(true)
              }}
            />
            Everything still owing
          </label>

          <label className="flex items-center gap-2 text-sm text-foreground">
            <input
              type="radio"
              name="scope"
              checked={!whole}
              onChange={() => {
                setWhole(false)
              }}
            />
            Some of it
          </label>
        </fieldset>

        {!whole ? (
          <div className="flex flex-col gap-2 rounded-lg border border-border p-3">
            {lines.map((line) => (
              <div key={line.id} className="flex items-center justify-between gap-3">
                <div className="min-w-0">
                  <p className="truncate text-sm text-foreground">{line.description}</p>
                  <p className="text-xs text-muted-foreground">
                    {formatQuantity(line.quantity)} sold ·{' '}
                    {formatMoney(line.lineTotal, currency)}
                  </p>
                </div>
                <Input
                  aria-label={`Return quantity for ${line.description}`}
                  inputMode="decimal"
                  className="w-24"
                  value={line.returning}
                  onChange={(event) => {
                    setQuantities((current) => ({
                      ...current,
                      [line.id]: event.target.value,
                    }))
                  }}
                />
              </div>
            ))}
            <p className="text-xs text-muted-foreground">
              Leave a line blank to keep it. The server re-prices the return from what was
              charged at the time, never from today&rsquo;s price.
            </p>
          </div>
        ) : null}

        <Field label="Reason" required hint="This is the record you will want in six months.">
          {(props) => (
            <Input
              {...props}
              value={reason}
              onChange={(event) => {
                setReason(event.target.value)
              }}
            />
          )}
        </Field>

        <div className="flex justify-end gap-2">
          <Button variant="ghost" onClick={onClose} disabled={refund.isPending}>
            Cancel
          </Button>
          <Button
            disabled={!canSubmit}
            onClick={() => {
              if (shift.data === undefined || shift.registerId === null) {
                return
              }

              refund.mutate(
                {
                  saleId: sale.id ?? '',
                  registerId: shift.registerId,
                  shiftId: shift.data.id,
                  reason: reason.trim(),
                  lines: whole ? null : partial,
                },
                {
                  onSuccess: (created) => {
                    toast.show(`Refunded as sale #${String(created.saleNumber ?? '—')}.`, {
                      tone: 'info',
                    })
                    onRefunded(created.id ?? '')
                  },
                  // Left open on failure, with the amounts as they were: the key
                  // is unchanged, so pressing again is a retry rather than a
                  // second refund.
                  onError: (caught) => {
                    toast.showError(caught, 'That refund did not go through.')
                  },
                },
              )
            }}
          >
            {refund.isPending ? 'Refunding…' : 'Refund'}
          </Button>
        </div>
      </div>
    </div>
  )
}
