import { useState, type FormEvent } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { api, unwrap } from '@/api/client'
import { Button } from '@/components/ui/button'
import { Field } from '@/components/ui/field'
import { Input, Select } from '@/components/ui/input'
import { ConfirmButton } from '@/components/ConfirmButton'
import { EmptyState, ErrorState, LoadingState } from '@/components/states'
import { useToast } from '@/components/toastContext'
import { catalogKeys, useAllCategories } from './queries'
import { validateCategoryName } from './validation'

export function CategoriesPage() {
  const queryClient = useQueryClient()
  const toast = useToast()

  const [name, setName] = useState('')
  const [parentCategoryId, setParentCategoryId] = useState('')
  const [error, setError] = useState<string | undefined>(undefined)

  const categories = useAllCategories()

  const invalidate = () => queryClient.invalidateQueries({ queryKey: catalogKeys.categories() })

  const create = useMutation({
    mutationFn: () =>
      unwrap(
        api.POST('/api/v1/categories', {
          body: {
            name: name.trim(),
            parentCategoryId: parentCategoryId === '' ? null : parentCategoryId,
            sortOrder: null,

            // A new category routes nowhere until somebody says where. Null is the
            // honest answer rather than a guess — see StationRouting: an unrouted
            // item is refused at the pass by name, which a manager can fix, and a
            // default station would send it somewhere silently instead.
            stationId: null,
          },
        }),
      ),
    onSuccess: async () => {
      setName('')
      setParentCategoryId('')
      setError(undefined)
      await invalidate()
      toast.show('Category added.', { tone: 'success' })
    },
    onError: (caught) => {
      toast.showError(caught, 'Could not add the category.')
    },
  })

  const setActive = useMutation({
    mutationFn: ({ id, active }: { id: string; active: boolean }) =>
      unwrap(
        active
          ? api.POST('/api/v1/categories/{id}/activate', { params: { path: { id } } })
          : api.POST('/api/v1/categories/{id}/deactivate', { params: { path: { id } } }),
      ),
    onSuccess: async () => {
      await invalidate()
    },
    onError: (caught) => {
      toast.showError(caught, 'Could not change the category.')
    },
  })

  function onSubmit(event: FormEvent) {
    event.preventDefault()

    const found = validateCategoryName(name)
    setError(found)

    if (found === undefined) {
      create.mutate()
    }
  }

  return (
    <div className="mx-auto flex max-w-2xl flex-col gap-6 p-6">
      <header>
        <h1 className="text-xl font-semibold text-foreground">Categories</h1>
        <p className="mt-1 text-sm text-muted-foreground">
          Optional. A product with no category still sells — this is for grouping the register grid
          and for filtering reports.
        </p>
      </header>

      <form onSubmit={onSubmit} className="flex items-end gap-2">
        <Field label="Name" error={error} className="flex-1">
          {(fieldProps) => (
            <Input
              {...fieldProps}
              value={name}
              onChange={(event) => {
                setName(event.target.value)
              }}
            />
          )}
        </Field>

        <Field label="Inside" className="w-48">
          {(fieldProps) => (
            <Select
              {...fieldProps}
              value={parentCategoryId}
              onChange={(event) => {
                setParentCategoryId(event.target.value)
              }}
            >
              <option value="">Top level</option>
              {(categories.data ?? []).map((category) => (
                <option key={category.id} value={category.id}>
                  {category.name}
                </option>
              ))}
            </Select>
          )}
        </Field>

        <Button type="submit" className="mb-0.5" disabled={create.isPending}>
          Add
        </Button>
      </form>

      {categories.isPending ? (
        <LoadingState />
      ) : categories.isError ? (
        <ErrorState
          error={categories.error}
          onRetry={() => {
            void categories.refetch()
          }}
          title="Could not load the categories."
        />
      ) : categories.data.length === 0 ? (
        <EmptyState
          title="No categories yet."
          description="Add one above, or leave products uncategorised."
        />
      ) : (
        <ul className="flex flex-col gap-2">
          {categories.data.map((category) => (
            <li
              key={category.id}
              className="flex items-center justify-between rounded-lg border border-border bg-card px-3 py-2"
            >
              <div>
                <p className="text-sm font-medium text-card-foreground">{category.name}</p>
                {category.parentCategoryId !== null ? (
                  <p className="text-xs text-muted-foreground">
                    inside{' '}
                    {categories.data.find((c) => c.id === category.parentCategoryId)?.name ??
                      'another category'}
                  </p>
                ) : null}
              </div>

              {category.isActive ? (
                <ConfirmButton
                  confirmLabel="Really hide?"
                  onConfirm={() => {
                    setActive.mutate({ id: category.id, active: false })
                  }}
                >
                  Hide
                </ConfirmButton>
              ) : (
                <Button
                  variant="outline"
                  onClick={() => {
                    setActive.mutate({ id: category.id, active: true })
                  }}
                >
                  Show again
                </Button>
              )}
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}
