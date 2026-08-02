import { useState, type FormEvent } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { api, unwrap } from '@/api/client'
import { newIdempotencyKey } from '@/api/idempotency'
import { isProblemError } from '@/api/problem'
import { Button } from '@/components/ui/button'
import { Field } from '@/components/ui/field'
import { Input, Select } from '@/components/ui/input'
import { useToast } from '@/components/toastContext'
import { formatQuantity } from '@/lib/money'
import { isStorableAmount, type FieldErrors } from './validation'

/**
 * The manual adjustment types a person may write.
 *
 * Mirrors `StockRules.IsManualAdjustment`. `Sale` and `Refund` are written by
 * the sale that caused them and carry a `SaleId` — offering them here would let
 * someone fabricate sales movements with no sale behind them, and the ledger
 * would stop reconciling with the takings. `Recount` states an absolute count
 * and the movement is a delta, which is a count-sheet feature that does not
 * exist yet.
 */
const TYPES = [
  { value: 'Receive', label: 'Received stock', sign: 1, hint: 'A delivery arrived.' },
  { value: 'Waste', label: 'Waste or breakage', sign: -1, hint: 'Damaged, expired or spilt.' },
  { value: 'Adjust', label: 'Correction', sign: 0, hint: 'The count was wrong. Sign matters.' },
] as const

export function StockAdjustmentDialog({
  productId,
  productName,
  onClose,
}: {
  productId: string
  productName: string
  onClose: () => void
}) {
  const queryClient = useQueryClient()
  const toast = useToast()

  const [type, setType] = useState<(typeof TYPES)[number]['value']>('Receive')
  const [quantity, setQuantity] = useState('')
  const [reason, setReason] = useState('')
  const [errors, setErrors] = useState<FieldErrors>({})

  /**
   * Minted once, when the dialog opens — **not** per attempt.
   *
   * That is the whole mechanism (CLAUDE.md invariant 6). A key generated inside
   * the fetch would be new on every retry, so the server would see new work and
   * the stock would move twice. Held in state so a retry after a timeout reuses
   * it and replays the original response.
   */
  const [idempotencyKey] = useState(newIdempotencyKey)

  const adjust = useMutation({
    mutationFn: () =>
      unwrap(
        api.POST('/api/v1/stock/adjustments', {
          params: { header: { 'Idempotency-Key': idempotencyKey } },
          body: {
            productId,
            type,
            quantity: signedQuantity(),
            reason: reason.trim(),
          },
        }),
      ),
    onSuccess: async (result) => {
      await queryClient.invalidateQueries({ queryKey: ['stock'] })
      toast.show(`${productName} is now ${formatQuantity(result.onHand)} on hand.`, {
        tone: 'success',
      })
      onClose()
    },
    onError: (caught) => {
      if (isProblemError(caught)) {
        const fieldErrors = caught.fieldErrors

        if (Object.keys(fieldErrors).length > 0) {
          setErrors(
            Object.fromEntries(
              Object.entries(fieldErrors).map(([field, messages]) => [field, messages[0] ?? '']),
            ),
          )
          return
        }
      }

      toast.showError(caught, 'Could not adjust the stock.')
    },
  })

  /**
   * The signed quantity the API expects.
   *
   * `StockMovement.Quantity` is signed rather than a magnitude plus a direction
   * implied by the type, and `StockRules.IsSignConsistent` refuses a receipt of
   * −5 or a waste of +5. So the two directional types get their sign applied
   * here and the user types a plain positive number; a correction is the one
   * case where the user states the direction, because both are meaningful.
   */
  function signedQuantity(): number {
    const magnitude = Number(quantity)
    const chosen = TYPES.find((option) => option.value === type)

    if (chosen === undefined || chosen.sign === 0) {
      return magnitude
    }

    return Math.abs(magnitude) * chosen.sign
  }

  function onSubmit(event: FormEvent) {
    event.preventDefault()

    const found: FieldErrors = {}
    const trimmedQuantity = quantity.trim()
    const isCorrection = type === 'Adjust'

    // A correction may be negative; the others may not, because their sign is
    // applied for the user.
    const magnitude = isCorrection ? trimmedQuantity.replace(/^-/, '') : trimmedQuantity

    if (trimmedQuantity === '') {
      found['quantity'] = 'A quantity is required.'
    } else if (!isStorableAmount(magnitude)) {
      found['quantity'] = 'A quantity with at most 4 decimal places is required.'
    } else if (Number(trimmedQuantity) === 0) {
      // A movement that moves nothing is a row that will be read as evidence of
      // something happening — see StockRules.IsSignConsistent.
      found['quantity'] = 'A movement of zero would record nothing.'
    }

    // Required here as well as at the API, and for the same reason: in six
    // months this row is the only explanation of why the number changed.
    if (reason.trim() === '') {
      found['reason'] = 'A reason is required — it is the only record of why this changed.'
    }

    setErrors(found)

    if (Object.keys(found).length === 0) {
      adjust.mutate()
    }
  }

  const selected = TYPES.find((option) => option.value === type)

  return (
    <div
      role="dialog"
      aria-modal="true"
      aria-labelledby="adjust-title"
      className="fixed inset-0 z-40 flex items-center justify-center bg-background/80 p-4 backdrop-blur-sm"
    >
      <form
        onSubmit={onSubmit}
        className="w-full max-w-md rounded-xl border border-border bg-card p-6 shadow-lg"
      >
        <h2 id="adjust-title" className="text-lg font-semibold text-card-foreground">
          Adjust stock
        </h2>
        <p className="mt-0.5 text-sm text-muted-foreground">{productName}</p>

        <div className="mt-5 flex flex-col gap-4">
          <Field label="What happened" required hint={selected?.hint}>
            {(fieldProps) => (
              <Select
                {...fieldProps}
                value={type}
                onChange={(event) => {
                  setType(event.target.value as (typeof TYPES)[number]['value'])
                }}
              >
                {TYPES.map((option) => (
                  <option key={option.value} value={option.value}>
                    {option.label}
                  </option>
                ))}
              </Select>
            )}
          </Field>

          <Field
            label={type === 'Adjust' ? 'Change (may be negative)' : 'Quantity'}
            required
            error={errors['quantity']}
          >
            {(fieldProps) => (
              <Input
                {...fieldProps}
                inputMode="decimal"
                autoFocus
                className="tabular-nums"
                value={quantity}
                onChange={(event) => {
                  setQuantity(event.target.value)
                }}
              />
            )}
          </Field>

          <Field label="Reason" required error={errors['reason']}>
            {(fieldProps) => (
              <Input
                {...fieldProps}
                placeholder="Delivery note 4471"
                value={reason}
                onChange={(event) => {
                  setReason(event.target.value)
                }}
              />
            )}
          </Field>
        </div>

        <div className="mt-6 flex justify-end gap-2">
          <Button type="button" variant="outline" onClick={onClose} disabled={adjust.isPending}>
            Cancel
          </Button>
          <Button type="submit" disabled={adjust.isPending}>
            {adjust.isPending ? 'Recording…' : 'Record movement'}
          </Button>
        </div>
      </form>
    </div>
  )
}
