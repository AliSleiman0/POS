import { useState, type FormEvent } from 'react'
import { Link } from 'react-router'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, unwrap } from '@/api/client'
import { Button } from '@/components/ui/button'
import { ConfirmButton } from '@/components/ConfirmButton'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { EmptyState, ErrorState, LoadingState } from '@/components/states'
import { useToast } from '@/components/toastContext'
import { getEnrolledRegisterId } from '@/auth/deviceToken'

/**
 * The tills a shop owns, across every device.
 *
 * Distinct from `/settings/device`, which is about *this* browser: that page
 * enrols the tablet you are holding and captures the token straight into
 * storage. This one is the fleet view — create a till, and revoke one whose
 * tablet was lost. Revoking from here deliberately does **not** touch local
 * storage, because the device you are revoking is somewhere else.
 */
export function RegisterListPage() {
  const queryClient = useQueryClient()
  const toast = useToast()

  const [name, setName] = useState('')
  const [nameError, setNameError] = useState<string | undefined>(undefined)

  const registers = useQuery({
    queryKey: ['registers'],
    // A bare array, not a cursor page — see docs/API.md.
    queryFn: () => unwrap(api.GET('/api/v1/registers')),
  })

  const create = useMutation({
    mutationFn: (registerName: string) =>
      unwrap(api.POST('/api/v1/registers', { body: { name: registerName } })),
    onSuccess: async (created) => {
      await queryClient.invalidateQueries({ queryKey: ['registers'] })
      setName('')
      toast.show(`${created.name} is ready to enrol.`, { tone: 'success' })
    },
    onError: (error) => {
      toast.showError(error, 'Could not create that till.')
    },
  })

  const revoke = useMutation({
    mutationFn: (id: string) =>
      unwrap(api.POST('/api/v1/registers/{id}/revoke', { params: { path: { id } } })),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['registers'] })
      toast.show('That till’s device token no longer works.')
    },
    onError: (error) => {
      toast.showError(error, 'Could not revoke that till.')
    },
  })

  function onCreate(event: FormEvent) {
    event.preventDefault()

    const trimmed = name.trim()

    if (trimmed === '') {
      setNameError('A name is required.')
      return
    }

    setNameError(undefined)
    create.mutate(trimmed)
  }

  const thisDevice = getEnrolledRegisterId()

  return (
    <div className="mx-auto flex max-w-3xl flex-col gap-8 p-6">
      <header>
        <h1 className="text-xl font-semibold text-foreground">Tills</h1>
        <p className="mt-1 text-sm text-muted-foreground">
          A till is a named drawer. Every sale and every shift belongs to one, which is what a
          Z-report is grouped by. To turn <em>this</em> browser into one, use{' '}
          <Link
            to="/settings/device"
            className="text-foreground underline underline-offset-4 hover:no-underline"
          >
            device setup
          </Link>
          .
        </p>
      </header>

      <form onSubmit={onCreate} className="flex flex-wrap items-end gap-3">
        <Field label="New till" error={nameError} className="min-w-56">
          {(fieldProps) => (
            <Input
              {...fieldProps}
              placeholder="Front counter"
              value={name}
              onChange={(event) => {
                setName(event.target.value)
              }}
            />
          )}
        </Field>
        <Button type="submit" disabled={create.isPending}>
          {create.isPending ? 'Adding…' : 'Add till'}
        </Button>
      </form>

      {registers.isPending ? (
        <LoadingState label="Loading the tills…" />
      ) : registers.isError ? (
        <ErrorState
          error={registers.error}
          onRetry={() => {
            void registers.refetch()
          }}
          title="Could not load the tills."
        />
      ) : registers.data.length === 0 ? (
        <EmptyState
          title="No tills yet."
          description="Add one above, then enrol a tablet against it."
        />
      ) : (
        <ul className="flex flex-col gap-2">
          {registers.data.map((register) => (
            <li
              key={register.id}
              className="flex items-center justify-between gap-4 rounded-lg border border-border bg-card p-3"
              data-testid="register-row"
            >
              <div>
                <p className="text-sm font-medium text-card-foreground">
                  {register.name}
                  {register.id === thisDevice ? (
                    <span className="ml-2 text-xs font-normal text-muted-foreground">
                      this device
                    </span>
                  ) : null}
                </p>
                <p className="text-xs text-muted-foreground">
                  {register.isEnrolled ? 'Enrolled' : 'Not enrolled'}
                  {register.lastSeenAt === null
                    ? ''
                    : ` · last seen ${new Date(register.lastSeenAt).toLocaleString()}`}
                </p>
              </div>

              {register.isEnrolled ? (
                <ConfirmButton
                  confirmLabel="Really revoke?"
                  onConfirm={() => {
                    revoke.mutate(register.id)
                  }}
                >
                  Revoke
                </ConfirmButton>
              ) : null}
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
