import { useState, type FormEvent } from 'react'
import { Link } from 'react-router'
import { newIdempotencyKey } from '@/api/idempotency'
import { isProblemError, ErrorType } from '@/api/problem'
import { Button, buttonVariants } from '@/components/ui/button'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { useToast } from '@/components/toastContext'
import { isStorableAmount } from '@/features/catalog/validation'
import { useOpenShift } from './queries'

/**
 * Opening the drawer.
 *
 * **The first action of the day, so it is on the screen rather than in a menu.**
 * A sale needs an open shift — without one there is nothing to reconcile the
 * drawer against and "we're £12 short" is unanswerable — and a cashier who has
 * to hunt for this at 7am with a queue forming will conclude the till is broken.
 */
export function OpenShiftPanel({
  registerId,
  currency,
}: {
  registerId: string | null
  currency: string
}) {
  const toast = useToast()
  const [openingFloat, setOpeningFloat] = useState('')
  const [error, setError] = useState<string | undefined>(undefined)

  /**
   * Minted once for the life of this panel — **not** per attempt.
   *
   * That is the mechanism (CLAUDE.md invariant 6): a key created inside the
   * fetch would be new on every retry, and a timeout followed by a retry would
   * open a second shift on the same drawer.
   */
  const [idempotencyKey] = useState(newIdempotencyKey)
  const open = useOpenShift(idempotencyKey)

  if (registerId === null) {
    return (
      <section className="flex flex-col gap-3 rounded-xl border border-border bg-card p-4">
        <h2 className="text-sm font-semibold text-card-foreground">This browser is not a till</h2>
        <p className="text-sm text-muted-foreground">
          A register has to be enrolled on this device before a drawer can be opened on it. An owner
          or manager can do that from the till settings.
        </p>
        <Link to="/settings/device" className={buttonVariants({ variant: 'outline' })}>
          Set up this till
        </Link>
      </section>
    )
  }

  function onSubmit(event: FormEvent) {
    event.preventDefault()

    const value = openingFloat.trim()

    if (value === '') {
      setError('A float is required — enter 0 if the drawer starts empty.')
      return
    }

    if (!isStorableAmount(value)) {
      setError('An amount of 0 or more with at most 4 decimal places is required.')
      return
    }

    setError(undefined)

    open.mutate(
      { registerId: registerId!, openingFloat: Number(value) },
      {
        onSuccess: () => {
          toast.show('Drawer open. Ready to sell.', { tone: 'success' })
        },
        onError: (caught) => {
          // Someone else opened it first — two tills, or a second tab. The
          // filtered unique index is what actually decides this, not a check.
          if (isProblemError(caught) && caught.is(ErrorType.shiftAlreadyOpen)) {
            toast.show('A drawer is already open on this register.', { tone: 'info' })
            return
          }

          toast.showError(caught, 'Could not open the drawer.')
        },
      },
    )
  }

  return (
    <section
      aria-label="Open the drawer"
      className="flex flex-col gap-3 rounded-xl border border-border bg-card p-4"
    >
      <div>
        <h2 className="text-sm font-semibold text-card-foreground">No drawer is open</h2>
        <p className="mt-0.5 text-sm text-muted-foreground">
          Count the float and open the drawer to start selling.
        </p>
      </div>

      <form onSubmit={onSubmit} className="flex flex-col gap-3">
        <Field label={`Opening float (${currency})`} required error={error}>
          {(fieldProps) => (
            <Input
              {...fieldProps}
              inputMode="decimal"
              autoFocus
              className="h-12 text-lg tabular-nums"
              placeholder="100.00"
              value={openingFloat}
              onChange={(event) => {
                setOpeningFloat(event.target.value)
              }}
            />
          )}
        </Field>

        <Button type="submit" size="lg" className="h-12 text-base" disabled={open.isPending}>
          {open.isPending ? 'Opening…' : 'Open the drawer'}
        </Button>
      </form>
    </section>
  )
}
