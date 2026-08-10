import { useState, type FormEvent } from 'react'
import { isProblemError } from '@/api/problem'
import { Button } from '@/components/ui/button'
import { Field } from '@/components/ui/field'
import { Input, Select } from '@/components/ui/input'
import { useToast } from '@/components/toastContext'
import { mergeFieldErrors } from '@/features/catalog/validation'
import {
  ROLES,
  useCreateEmployee,
  useUpdateEmployee,
  type EmployeeRole,
  type EmployeeSummary,
} from './queries'
import { validateExistingEmployee, validateNewEmployee, type FieldErrors } from './validation'

/**
 * Add somebody, or change who they are.
 *
 * One dialog for both, because the fields overlap almost entirely and the two
 * differing rules are worth stating in one place: **email is set once** (it is
 * the login identity, and changing it silently locks somebody out of a session
 * they are holding), and **a password is only set at creation** (there is no
 * mail infrastructure, so an owner reads the initial password out; changing it
 * later is the person's own job and needs a flow this phase does not build).
 */
export function EmployeeFormDialog({
  employee,
  onClose,
}: {
  employee: EmployeeSummary | null
  onClose: () => void
}) {
  const toast = useToast()
  const isNew = employee === null

  const [displayName, setDisplayName] = useState(employee?.displayName ?? '')
  const [email, setEmail] = useState(employee?.email ?? '')
  const [role, setRole] = useState<EmployeeRole>(
    (employee?.role as EmployeeRole | null) ?? 'Cashier',
  )
  const [isActive, setIsActive] = useState(employee?.isActive ?? true)
  const [password, setPassword] = useState('')
  const [pin, setPin] = useState('')
  const [errors, setErrors] = useState<FieldErrors>({})

  const create = useCreateEmployee()
  const update = useUpdateEmployee()
  const saving = create.isPending || update.isPending

  function onFailure(caught: unknown, fallback: string) {
    if (isProblemError(caught)) {
      const fieldErrors = caught.fieldErrors

      if (Object.keys(fieldErrors).length > 0) {
        setErrors((current) => mergeFieldErrors(current, fieldErrors))
        return
      }
    }

    // The lock-out guards (self-demotion, last-owner) arrive here: they carry no
    // errors map, because there is no field to blame. A toast is the right
    // shape — it says what the shop's state prevents, not what the user typed.
    toast.showError(caught, fallback)
  }

  function onSubmit(event: FormEvent) {
    event.preventDefault()

    const found = isNew
      ? validateNewEmployee({ displayName, email, password, pin })
      : validateExistingEmployee({ displayName })

    setErrors(found)

    if (Object.keys(found).length > 0) {
      return
    }

    if (isNew) {
      create.mutate(
        {
          displayName: displayName.trim(),
          email: email.trim(),
          role,
          password,
          pin: pin === '' ? null : pin,
        },
        {
          onSuccess: (result) => {
            toast.show(`${result.displayName} can now sign in.`, { tone: 'success' })
            onClose()
          },
          onError: (caught) => {
            onFailure(caught, 'Could not add this person.')
          },
        },
      )

      return
    }

    update.mutate(
      {
        id: employee.id,
        body: { displayName: displayName.trim(), role, isActive },
      },
      {
        onSuccess: () => {
          toast.show('Saved.', { tone: 'success' })
          onClose()
        },
        onError: (caught) => {
          onFailure(caught, 'Could not save this person.')
        },
      },
    )
  }

  const selectedRole = ROLES.find((option) => option.value === role)

  return (
    <div
      role="dialog"
      aria-modal="true"
      aria-labelledby="employee-title"
      className="fixed inset-0 z-40 flex items-center justify-center bg-background/80 p-4 backdrop-blur-sm"
    >
      <form
        onSubmit={onSubmit}
        className="w-full max-w-md rounded-xl border border-border bg-card p-6 shadow-lg"
      >
        <h2 id="employee-title" className="text-lg font-semibold text-card-foreground">
          {isNew ? 'Add somebody' : 'Edit person'}
        </h2>
        {isNew ? null : <p className="mt-0.5 text-sm text-muted-foreground">{employee.email}</p>}

        <div className="mt-5 flex flex-col gap-4">
          <Field label="Name" required error={errors['displayName']}>
            {(fieldProps) => (
              <Input
                {...fieldProps}
                autoFocus
                value={displayName}
                onChange={(event) => {
                  setDisplayName(event.target.value)
                }}
              />
            )}
          </Field>

          {isNew ? (
            <Field
              label="Email"
              required
              error={errors['email']}
              hint="They sign in with this. It cannot be changed later."
            >
              {(fieldProps) => (
                <Input
                  {...fieldProps}
                  type="email"
                  autoComplete="off"
                  value={email}
                  onChange={(event) => {
                    setEmail(event.target.value)
                  }}
                />
              )}
            </Field>
          ) : null}

          <Field label="Role" required hint={selectedRole?.hint}>
            {(fieldProps) => (
              <Select
                {...fieldProps}
                value={role}
                onChange={(event) => {
                  setRole(event.target.value as EmployeeRole)
                }}
              >
                {ROLES.map((option) => (
                  <option key={option.value} value={option.value}>
                    {option.label}
                  </option>
                ))}
              </Select>
            )}
          </Field>

          {isNew ? (
            <>
              <Field
                label="Initial password"
                required
                error={errors['password']}
                hint="Read it out to them. There is no email invite."
              >
                {(fieldProps) => (
                  <Input
                    {...fieldProps}
                    type="text"
                    autoComplete="off"
                    value={password}
                    onChange={(event) => {
                      setPassword(event.target.value)
                    }}
                  />
                )}
              </Field>

              <Field
                label="Till PIN (optional)"
                error={errors['pin']}
                hint="Only needed if they will use the register."
              >
                {(fieldProps) => (
                  <Input
                    {...fieldProps}
                    inputMode="numeric"
                    autoComplete="off"
                    className="max-w-32 tabular-nums"
                    value={pin}
                    onChange={(event) => {
                      setPin(event.target.value)
                    }}
                  />
                )}
              </Field>
            </>
          ) : (
            <label className="flex items-center gap-2 text-sm text-muted-foreground">
              <input
                type="checkbox"
                className="size-4"
                checked={isActive}
                onChange={(event) => {
                  setIsActive(event.target.checked)
                }}
              />
              Can sign in
            </label>
          )}
        </div>

        <div className="mt-6 flex justify-end gap-2">
          <Button type="button" variant="outline" onClick={onClose} disabled={saving}>
            Cancel
          </Button>
          <Button type="submit" disabled={saving}>
            {saving ? 'Saving…' : isNew ? 'Add' : 'Save'}
          </Button>
        </div>
      </form>
    </div>
  )
}
