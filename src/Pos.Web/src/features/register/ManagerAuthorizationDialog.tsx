import { useState, type FormEvent } from 'react'
import { useQuery } from '@tanstack/react-query'
import { deviceApi, unwrap } from '@/api/client'
import { ErrorType, isProblemError } from '@/api/problem'
import { isDeviceEnrolled } from '@/auth/deviceToken'
import type { Policy } from '@/auth/policies'
import { Button } from '@/components/ui/button'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { EmptyState, ErrorState, LoadingState } from '@/components/states'
import type { OverrideAuthorization } from './overrideContext'

/**
 * A manager approving one action on the cashier's session.
 *
 * The cashier stays signed in throughout. `POST /auth/override` verifies the
 * manager's PIN against the till's device token and returns a single-use grant;
 * the sale is still rung by, and attributed to, whoever is on the till.
 *
 * **In-page, never `prompt()`** (CLAUDE.md invariant 10). A blocking dialog
 * stalls the event loop, and a scanner firing behind one queues its keystrokes
 * and replays them into whatever has focus when it closes. Standing the scanner
 * down needs no extra work here: the PIN field is an `<input>`, and
 * `isEditableTarget` already makes `useScanner` stand aside for one.
 */
export function ManagerAuthorizationDialog({
  policies,
  onSettle,
}: {
  policies: readonly Policy[]
  onSettle: (authorization: OverrideAuthorization | null) => void
}) {
  const [userId, setUserId] = useState<string | null>(null)
  const [pin, setPin] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)

  const enrolled = isDeviceEnrolled()

  // The same key `PinSwapPage` uses, so the staff list is fetched once per till
  // rather than again every time a manager is called over.
  const employees = useQuery({
    queryKey: ['employees', 'pin-eligible'],
    queryFn: () => unwrap(deviceApi.GET('/api/v1/employees/pin-eligible')),
    enabled: enrolled,
  })

  const what = describe(policies)

  async function onSubmit(event: FormEvent) {
    event.preventDefault()

    if (userId === null) {
      return
    }

    setSubmitting(true)
    setError(null)

    try {
      const granted = await unwrap(
        deviceApi.POST('/api/v1/auth/override', {
          body: { userId, pin, policies: [...policies] },
        }),
      )

      onSettle({
        grant: granted.grant,
        policies: granted.policies as Policy[],
        authorizedByName: granted.authorizedByName,
      })
    } catch (caught) {
      setPin('')

      if (isProblemError(caught)) {
        if (caught.is(ErrorType.accountLocked)) {
          const until = caught.problem.lockoutEndsAt

          setError(
            until === undefined
              ? 'Too many failed attempts on that account.'
              : `Too many failed attempts. Try again after ${new Date(until).toLocaleTimeString()}.`,
          )
          return
        }

        if (caught.is(ErrorType.overrideNotPermitted)) {
          // The PIN was right and the answer is still no. Said plainly, because
          // "wrong PIN" would send a manager away convinced they mistyped.
          setError(`${nameOf(employees.data, userId)} cannot authorise this.`)
          return
        }

        setError('That PIN was not recognised.')
        return
      }

      setError('Could not reach the server. Nothing has been changed.')
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <div
      role="dialog"
      aria-modal="true"
      aria-labelledby="manager-override-title"
      data-testid="manager-override"
      className="fixed inset-0 z-40 flex items-center justify-center bg-background/80 p-4 backdrop-blur-sm"
    >
      <div className="w-full max-w-md rounded-xl border border-border bg-card p-6 shadow-lg">
        <h2 id="manager-override-title" className="text-lg font-semibold text-card-foreground">
          A manager has to approve {what}
        </h2>
        <p className="mt-1 text-sm text-muted-foreground">
          They enter their PIN here. You stay signed in, and the sale stays yours.
        </p>

        {!enrolled ? (
          <EmptyState
            className="mt-6"
            title="This browser is not a till."
            description="A manager's PIN only authorises anything from a device an owner has enrolled — the PIN is never the whole credential. Sign in as a manager instead."
          />
        ) : employees.isPending ? (
          <LoadingState />
        ) : employees.isError ? (
          <ErrorState
            error={employees.error}
            title="Could not load the staff list."
            onRetry={() => {
              void employees.refetch()
            }}
          />
        ) : (
          <>
            {/* Everyone with a PIN is listed, because `/employees/pin-eligible`
                deliberately does not say who is a manager — a list readable from
                a counter must not become the shop's org chart. Picking the wrong
                person costs one refusal. */}
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
                <Field label="Manager PIN" error={error ?? undefined}>
                  {(fieldProps) => (
                    <Input
                      {...fieldProps}
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
                  {submitting ? 'Checking…' : 'Approve'}
                </Button>
              </form>
            ) : null}
          </>
        )}

        <Button
          variant="ghost"
          className="mt-4 w-full"
          onClick={() => {
            onSettle(null)
          }}
        >
          Cancel
        </Button>
      </div>
    </div>
  )
}

/** What is being approved, in the words a cashier would use. */
function describe(policies: readonly Policy[]): string {
  const discount = policies.includes('CanApplyDiscount')
  const override = policies.includes('CanOverridePrice')

  if (discount && override) {
    return 'this discount and price change'
  }

  return override ? 'this price change' : 'this discount'
}

function nameOf(
  employees: { id: string; displayName: string }[] | undefined,
  userId: string,
): string {
  return employees?.find((employee) => employee.id === userId)?.displayName ?? 'That person'
}
