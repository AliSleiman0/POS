import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, unwrap } from '@/api/client'
import { Button } from '@/components/ui/button'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { ConfirmButton } from '@/components/ConfirmButton'
import { EmptyState, ErrorState, LoadingState } from '@/components/states'
import { useToast } from '@/components/toastContext'
import {
  clearDeviceToken,
  getEnrolledRegisterId,
  isDeviceEnrolled,
  setDeviceToken,
} from './deviceToken'

/**
 * Turn this browser into a till.
 *
 * Two ways in, because the token is shown **once** and both cases are real:
 *
 * 1. Enrol a register from here, and the token is captured straight into
 *    storage without a human ever seeing it.
 * 2. Paste a token issued elsewhere — by `tools/Pos.Seed`, or on another
 *    device — which is what you do when the tablet was wiped.
 *
 * Owner-only, because whoever can enrol a device can start PIN sessions on it.
 */
export function DeviceEnrollmentPage() {
  const queryClient = useQueryClient()
  const toast = useToast()

  const [pastedToken, setPastedToken] = useState('')
  const [pastedRegisterId, setPastedRegisterId] = useState('')

  const registers = useQuery({
    queryKey: ['registers'],
    // A bare array, not a cursor page — see docs/API.md.
    queryFn: () => unwrap(api.GET('/api/v1/registers')),
  })

  const enroll = useMutation({
    mutationFn: (registerId: string) =>
      unwrap(api.POST('/api/v1/registers/{id}/enroll', { params: { path: { id: registerId } } })),
    onSuccess: (result) => {
      // Captured directly. The token is never rendered, so it cannot be read
      // over a shoulder or left in a screenshot.
      setDeviceToken(result.deviceToken, result.registerId)
      void queryClient.invalidateQueries({ queryKey: ['registers'] })
      toast.show('This device is now enrolled.', { tone: 'success' })
    },
    onError: (error) => {
      toast.showError(error, 'Could not enrol this device.')
    },
  })

  const enrolledRegisterId = getEnrolledRegisterId()

  return (
    <div className="mx-auto flex max-w-2xl flex-col gap-8">
      <section>
        <h1 className="text-xl font-semibold text-foreground">This device</h1>

        {isDeviceEnrolled() ? (
          <div className="mt-3 flex items-center justify-between rounded-lg border border-border bg-card p-4">
            <div>
              <p className="text-sm font-medium text-card-foreground">Enrolled as a till</p>
              <p className="mt-0.5 font-mono text-xs text-muted-foreground">
                register {enrolledRegisterId}
              </p>
            </div>
            <ConfirmButton
              confirmLabel="Really forget?"
              onConfirm={() => {
                clearDeviceToken()
                toast.show('This device is no longer a till.')
              }}
            >
              Forget this device
            </ConfirmButton>
          </div>
        ) : (
          <EmptyState
            className="mt-3"
            title="Not enrolled."
            description="PIN login is unavailable until this browser holds a device token."
          />
        )}
      </section>

      <section>
        <h2 className="text-lg font-semibold text-foreground">Enrol a register</h2>
        <p className="mt-1 text-sm text-muted-foreground">
          Issues a new token and stores it here. Any device already using that register&rsquo;s old
          token stops working immediately.
        </p>

        {registers.isPending ? (
          <LoadingState />
        ) : registers.isError ? (
          <ErrorState
            error={registers.error}
            onRetry={() => {
              void registers.refetch()
            }}
            title="Could not load the registers."
          />
        ) : registers.data.length === 0 ? (
          <EmptyState className="mt-3" title="No registers yet." />
        ) : (
          <ul className="mt-3 flex flex-col gap-2">
            {registers.data.map((register) => (
              <li
                key={register.id}
                className="flex items-center justify-between rounded-lg border border-border bg-card p-3"
              >
                <div>
                  <p className="text-sm font-medium text-card-foreground">{register.name}</p>
                  <p className="text-xs text-muted-foreground">
                    {register.isEnrolled ? 'Enrolled somewhere' : 'Never enrolled'}
                    {register.isActive ? '' : ' · inactive'}
                  </p>
                </div>
                <Button
                  variant="outline"
                  disabled={enroll.isPending}
                  onClick={() => {
                    enroll.mutate(register.id)
                  }}
                >
                  Enrol here
                </Button>
              </li>
            ))}
          </ul>
        )}
      </section>

      <section>
        <h2 className="text-lg font-semibold text-foreground">Or paste an existing token</h2>
        <p className="mt-1 text-sm text-muted-foreground">
          Printed once by the seeder or by an earlier enrolment. Nothing is checked here — a wrong
          token fails at the PIN screen, not now.
        </p>

        <div className="mt-3 flex flex-col gap-3">
          <Field label="Register id">
            {(fieldProps) => (
              <Input
                {...fieldProps}
                value={pastedRegisterId}
                onChange={(event) => {
                  setPastedRegisterId(event.target.value.trim())
                }}
                placeholder="00000000-0000-0000-0000-000000000000"
                className="font-mono"
              />
            )}
          </Field>

          <Field label="Device token">
            {(fieldProps) => (
              <Input
                {...fieldProps}
                value={pastedToken}
                onChange={(event) => {
                  setPastedToken(event.target.value.trim())
                }}
                className="font-mono"
              />
            )}
          </Field>

          <Button
            className="self-start"
            disabled={pastedToken === '' || pastedRegisterId === ''}
            onClick={() => {
              setDeviceToken(pastedToken, pastedRegisterId)
              setPastedToken('')
              setPastedRegisterId('')
              toast.show('Token stored on this device.', { tone: 'success' })
            }}
          >
            Store token
          </Button>
        </div>
      </section>
    </div>
  )
}
