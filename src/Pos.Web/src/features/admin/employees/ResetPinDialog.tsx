import { useState, type FormEvent } from 'react'
import { isProblemError } from '@/api/problem'
import { Button } from '@/components/ui/button'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { useToast } from '@/components/toastContext'
import { useSetPin, type EmployeeSummary } from './queries'
import { validatePin, type FieldErrors } from './validation'

/**
 * Set or replace somebody's till PIN.
 *
 * Its own dialog rather than a field on the edit form, because it is a
 * different kind of act: editing somebody is a correction, and handing out a
 * credential is a decision. The audit log records this one and not a rename,
 * for the same reason.
 */
export function ResetPinDialog({
  employee,
  onClose,
}: {
  employee: EmployeeSummary
  onClose: () => void
}) {
  const toast = useToast()
  const [pin, setPin] = useState('')
  const [errors, setErrors] = useState<FieldErrors>({})

  const setPinMutation = useSetPin()

  function onSubmit(event: FormEvent) {
    event.preventDefault()

    const problem = validatePin(pin)

    setErrors(problem === undefined ? {} : { pin: problem })

    if (problem !== undefined) {
      return
    }

    setPinMutation.mutate(
      { id: employee.id, pin },
      {
        onSuccess: () => {
          toast.show(`${employee.displayName} can now sign in at the till.`, { tone: 'success' })
          onClose()
        },
        onError: (caught) => {
          if (isProblemError(caught)) {
            const fieldErrors = caught.fieldErrors

            if (fieldErrors['pin']?.[0] !== undefined) {
              setErrors({ pin: fieldErrors['pin'][0] })
              return
            }
          }

          toast.showError(caught, 'Could not set the PIN.')
        },
      },
    )
  }

  return (
    <div
      role="dialog"
      aria-modal="true"
      aria-labelledby="pin-title"
      className="fixed inset-0 z-40 flex items-center justify-center bg-background/80 p-4 backdrop-blur-sm"
    >
      <form
        onSubmit={onSubmit}
        className="w-full max-w-sm rounded-xl border border-border bg-card p-6 shadow-lg"
      >
        <h2 id="pin-title" className="text-lg font-semibold text-card-foreground">
          {employee.hasPin ? 'Replace PIN' : 'Set PIN'}
        </h2>
        <p className="mt-0.5 text-sm text-muted-foreground">{employee.displayName}</p>

        <div className="mt-5">
          <Field
            label="New PIN"
            required
            error={errors['pin']}
            // Two people sharing four digits is fine and is never reported —
            // "that PIN is taken" would hand whoever asked a working PIN for
            // somebody else's account. Said here so the absence looks deliberate.
            hint="Anything from 4 to 6 digits. It does not have to be unique."
          >
            {(fieldProps) => (
              <Input
                {...fieldProps}
                inputMode="numeric"
                autoComplete="off"
                autoFocus
                className="max-w-32 tabular-nums"
                value={pin}
                onChange={(event) => {
                  setPin(event.target.value)
                }}
              />
            )}
          </Field>
        </div>

        <div className="mt-6 flex justify-end gap-2">
          <Button
            type="button"
            variant="outline"
            onClick={onClose}
            disabled={setPinMutation.isPending}
          >
            Cancel
          </Button>
          <Button type="submit" disabled={setPinMutation.isPending}>
            {setPinMutation.isPending ? 'Saving…' : 'Set PIN'}
          </Button>
        </div>
      </form>
    </div>
  )
}
