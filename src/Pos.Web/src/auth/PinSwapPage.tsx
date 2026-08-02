import { useState, type FormEvent } from 'react'
import { Link, useNavigate } from 'react-router'
import { useQuery } from '@tanstack/react-query'
import { deviceApi, unwrap } from '@/api/client'
import { Button, buttonVariants } from '@/components/ui/button'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { EmptyState, ErrorState, LoadingState } from '@/components/states'
import { ErrorType, isProblemError } from '@/api/problem'
import { useAuth } from './authContext'
import { isDeviceEnrolled } from './deviceToken'

/**
 * PIN swap — the register's idle state, and the flow a shop actually uses all
 * day.
 *
 * Pick a name, enter a PIN, the session swaps. No full logout, no email
 * address typed between customers.
 *
 * Both calls here go through `deviceApi`, which sends `X-Device-Token` and
 * never an `Authorization` header. That is not a detail: the `EnrolledDevice`
 * policy names the DeviceToken scheme explicitly, so a bearer token would be
 * evaluated by the JWT scheme instead and the second factor would silently
 * vanish. A PIN is never sufficient authentication on its own.
 */
export function PinSwapPage() {
  const { pinLogin } = useAuth()
  const navigate = useNavigate()

  const [userId, setUserId] = useState<string | null>(null)
  const [pin, setPin] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)

  const enrolled = isDeviceEnrolled()

  const employees = useQuery({
    queryKey: ['employees', 'pin-eligible'],
    queryFn: () => unwrap(deviceApi.GET('/api/v1/employees/pin-eligible')),
    enabled: enrolled,
  })

  if (!enrolled) {
    return (
      <main className="flex h-full items-center justify-center p-4">
        <EmptyState
          className="max-w-md px-8"
          title="This device is not enrolled."
          description="PIN login only works from a till an owner has enrolled. Sign in with an email and password, then enrol this device from Settings."
          action={
            <Link to="/login" className={buttonVariants({ variant: 'outline', size: 'lg' })}>
              Sign in instead
            </Link>
          }
        />
      </main>
    )
  }

  async function onSubmit(event: FormEvent) {
    event.preventDefault()

    if (userId === null) {
      return
    }

    setSubmitting(true)
    setError(null)

    try {
      await pinLogin({ userId, pin })
      await navigate('/', { replace: true })
    } catch (caught) {
      setPin('')

      if (isProblemError(caught) && caught.is(ErrorType.accountLocked)) {
        // Told plainly, because by this point the attempt budget is spent and
        // the cashier needs to know to stop trying rather than keep guessing.
        const until = caught.problem.lockoutEndsAt
        setError(
          until === undefined
            ? 'Too many failed attempts. Ask a manager to reset the PIN.'
            : `Too many failed attempts. Try again after ${new Date(until).toLocaleTimeString()}.`,
        )
        return
      }

      setError(
        isProblemError(caught) ? 'That PIN was not recognised.' : 'Could not reach the server.',
      )
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <main className="flex h-full flex-col items-center justify-center gap-6 p-4">
      <div className="w-full max-w-md rounded-xl border border-border bg-card p-8 shadow-sm">
        <h1 className="text-2xl font-semibold text-card-foreground">Who&rsquo;s on the till?</h1>

        {employees.isPending ? (
          <LoadingState />
        ) : employees.isError ? (
          <ErrorState
            error={employees.error}
            onRetry={() => {
              void employees.refetch()
            }}
            title="Could not load the staff list."
          />
        ) : employees.data.length === 0 ? (
          <EmptyState
            className="mt-6"
            title="Nobody has a PIN yet."
            description="An owner sets PINs from Employees."
          />
        ) : (
          <>
            <div className="mt-6 grid grid-cols-2 gap-2">
              {employees.data.map((employee) => (
                <Button
                  key={employee.id}
                  type="button"
                  size="lg"
                  variant={userId === employee.id ? 'default' : 'outline'}
                  className="h-14 justify-start text-base"
                  onClick={() => {
                    setUserId(employee.id)
                    setPin('')
                    setError(null)
                  }}
                >
                  {employee.displayName}
                </Button>
              ))}
            </div>

            {userId !== null ? (
              <form
                onSubmit={(event) => {
                  void onSubmit(event)
                }}
                className="mt-6"
              >
                <Field label="PIN" error={error ?? undefined}>
                  {(fieldProps) => (
                    <Input
                      {...fieldProps}
                      // `inputMode` rather than type="number": a numeric input
                      // gets spinner arrows and accepts `e` and `-`.
                      inputMode="numeric"
                      type="password"
                      autoComplete="off"
                      autoFocus
                      maxLength={6}
                      pattern="\d{4,6}"
                      className="h-14 text-center text-2xl tracking-[0.5em]"
                      value={pin}
                      onChange={(event) => {
                        setPin(event.target.value.replaceAll(/\D/g, ''))
                      }}
                      required
                    />
                  )}
                </Field>

                <Button
                  type="submit"
                  size="lg"
                  className="mt-4 w-full"
                  disabled={submitting || pin.length < 4}
                >
                  {submitting ? 'Checking…' : 'Start shift session'}
                </Button>
              </form>
            ) : null}
          </>
        )}
      </div>

      <Link to="/login" className="text-sm text-muted-foreground underline underline-offset-4">
        Sign in with an email address instead
      </Link>
    </main>
  )
}
