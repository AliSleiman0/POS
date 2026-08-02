import { useState, type FormEvent } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { api, unwrap } from '@/api/client'
import { Button } from '@/components/ui/button'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { EmptyState, ErrorState, LoadingState } from '@/components/states'
import { useToast } from '@/components/toastContext'
import { useAuth } from '@/auth/authContext'
import { formatRate } from '@/lib/money'
import { catalogKeys, useAllTaxClasses } from './queries'
import { validateTaxClass, type FieldErrors } from './validation'

/**
 * Tax classes.
 *
 * Note what is missing: there is no deactivate. `TaxClassEndpoints` maps only
 * list, create and update, because every product points at one and a product
 * with no usable tax class cannot be priced at all. The UI reflects that rather
 * than offering a button that would 404.
 */
export function TaxClassesPage() {
  const queryClient = useQueryClient()
  const toast = useToast()
  const { tenant } = useAuth()

  const [name, setName] = useState('')
  const [rate, setRate] = useState('')
  const [isDefault, setIsDefault] = useState(false)
  const [errors, setErrors] = useState<FieldErrors>({})

  const taxClasses = useAllTaxClasses()

  const create = useMutation({
    mutationFn: () =>
      unwrap(
        api.POST('/api/v1/tax-classes', {
          body: { name: name.trim(), rate: Number(rate), isDefault },
        }),
      ),
    onSuccess: async () => {
      setName('')
      setRate('')
      setIsDefault(false)
      setErrors({})
      await queryClient.invalidateQueries({ queryKey: catalogKeys.taxClasses() })
      toast.show('Tax class added.', { tone: 'success' })
    },
    onError: (caught) => {
      toast.showError(caught, 'Could not add the tax class.')
    },
  })

  function onSubmit(event: FormEvent) {
    event.preventDefault()

    const found = validateTaxClass(name, rate)
    setErrors(found)

    if (Object.keys(found).length === 0) {
      create.mutate()
    }
  }

  return (
    <div className="mx-auto flex max-w-2xl flex-col gap-6 p-6">
      <header>
        <h1 className="text-xl font-semibold text-foreground">Tax classes</h1>
        <p className="mt-1 text-sm text-muted-foreground">
          Prices in this shop are{' '}
          <strong>{tenant?.taxMode === 'Inclusive' ? 'tax inclusive' : 'tax exclusive'}</strong>. A
          rate is a fraction — 20% is <code className="font-mono">0.2</code>.
        </p>
      </header>

      <form onSubmit={onSubmit} className="flex items-end gap-2">
        <Field label="Name" error={errors['name']} className="flex-1">
          {(fieldProps) => (
            <Input
              {...fieldProps}
              placeholder="Standard rate"
              value={name}
              onChange={(event) => {
                setName(event.target.value)
              }}
            />
          )}
        </Field>

        <Field label="Rate" error={errors['rate']} className="w-32">
          {(fieldProps) => (
            <Input
              {...fieldProps}
              inputMode="decimal"
              placeholder="0.2"
              className="tabular-nums"
              value={rate}
              onChange={(event) => {
                setRate(event.target.value)
              }}
            />
          )}
        </Field>

        <label className="mb-2 flex items-center gap-2 text-sm text-foreground">
          <input
            type="checkbox"
            className="size-4"
            checked={isDefault}
            onChange={(event) => {
              setIsDefault(event.target.checked)
            }}
          />
          Default
        </label>

        <Button type="submit" className="mb-0.5" disabled={create.isPending}>
          Add
        </Button>
      </form>

      {taxClasses.isPending ? (
        <LoadingState />
      ) : taxClasses.isError ? (
        <ErrorState
          error={taxClasses.error}
          onRetry={() => {
            void taxClasses.refetch()
          }}
          title="Could not load the tax classes."
        />
      ) : taxClasses.data.length === 0 ? (
        <EmptyState
          title="No tax classes yet."
          description="Add at least one — a product cannot be created without a tax class."
        />
      ) : (
        <ul className="flex flex-col gap-2">
          {taxClasses.data.map((taxClass) => (
            <li
              key={taxClass.id}
              className="flex items-center justify-between rounded-lg border border-border bg-card px-3 py-2"
            >
              <div>
                <p className="text-sm font-medium text-card-foreground">{taxClass.name}</p>
                {taxClass.isDefault ? (
                  <p className="text-xs text-muted-foreground">Used by default on new products</p>
                ) : null}
              </div>
              <span className="font-mono text-sm tabular-nums text-foreground">
                {formatRate(taxClass.rate)}
              </span>
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
