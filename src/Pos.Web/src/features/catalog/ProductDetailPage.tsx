import { useEffect, useState, type FormEvent } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { api, unwrap } from '@/api/client'
import { ErrorType, isProblemError } from '@/api/problem'
import { Button } from '@/components/ui/button'
import { Field } from '@/components/ui/field'
import { Input, Select, Textarea } from '@/components/ui/input'
import { ConfirmButton } from '@/components/ConfirmButton'
import { ErrorState, LoadingState } from '@/components/states'
import { useToast } from '@/components/toastContext'
import { IfPolicy } from '@/auth/guards'
import { useAuth } from '@/auth/authContext'
import { BarcodePanel } from './BarcodePanel'
import { catalogKeys, useAllCategories, useAllTaxClasses, useProduct } from './queries'
import {
  mergeFieldErrors,
  validateProduct,
  type FieldErrors,
  type ProductFormValues,
} from './validation'

const EMPTY: ProductFormValues = {
  sku: '',
  name: '',
  description: '',
  categoryId: '',
  taxClassId: '',
  unitPrice: '',
  costPrice: '',
  unit: 'Each',
  trackStock: true,
}

/**
 * Create or edit one product, plus its barcodes.
 *
 * `/catalog/products/new` is the create form; any other id is the edit form.
 */
export function ProductDetailPage() {
  const { productId = 'new' } = useParams()
  const isNew = productId === 'new'

  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const toast = useToast()
  const { can } = useAuth()

  const categories = useAllCategories()
  const taxClasses = useAllTaxClasses()
  const existing = useProduct(isNew ? '' : productId)

  const [values, setValues] = useState<ProductFormValues>(EMPTY)
  const [errors, setErrors] = useState<FieldErrors>({})
  const [loaded, setLoaded] = useState(false)

  // Seeded from the server's row once. Not a `key` remount: retyping the form
  // because a background refetch landed would be maddening mid-edit.
  useEffect(() => {
    if (isNew || existing.data === undefined || loaded) {
      return
    }

    const product = existing.data

    setValues({
      sku: product.sku,
      name: product.name,
      description: product.description ?? '',
      categoryId: product.categoryId ?? '',
      taxClassId: product.taxClassId,
      unitPrice: String(product.unitPrice),
      // Absent for a caller without CanViewMargins — see below.
      costPrice: product.costPrice === null ? '' : String(product.costPrice),
      unit: product.unit,
      trackStock: product.trackStock,
    })
    setLoaded(true)
  }, [existing.data, isNew, loaded])

  // A sensible default so "a tax class is required" is not the first thing a
  // new tenant sees on a form they have not filled in yet.
  useEffect(() => {
    if (!isNew || values.taxClassId !== '' || taxClasses.data === undefined) {
      return
    }

    const fallback = taxClasses.data.find((taxClass) => taxClass.isDefault) ?? taxClasses.data[0]

    if (fallback !== undefined) {
      setValues((current) => ({ ...current, taxClassId: fallback.id }))
    }
  }, [isNew, taxClasses.data, values.taxClassId])

  const body = () => ({
    sku: values.sku.trim(),
    name: values.name.trim(),
    description: values.description.trim() === '' ? null : values.description.trim(),
    categoryId: values.categoryId === '' ? null : values.categoryId,
    taxClassId: values.taxClassId,
    unitPrice: Number(values.unitPrice),
    // Omitted rather than zeroed when blank. A caller who cannot see cost gets
    // it back absent from their GET, so sending `0` would wipe the Owner's cost
    // data on every edit a Manager made.
    costPrice: values.costPrice.trim() === '' ? null : Number(values.costPrice),
    unit: values.unit,
    trackStock: values.trackStock,
  })

  const save = useMutation({
    mutationFn: async () => {
      if (isNew) {
        return unwrap(api.POST('/api/v1/products', { body: body() }))
      }

      return unwrap(
        api.PUT('/api/v1/products/{id}', { params: { path: { id: productId } }, body: body() }),
      )
    },
    onSuccess: async (product) => {
      await queryClient.invalidateQueries({ queryKey: ['products'] })
      toast.show(isNew ? 'Product created.' : 'Product saved.', { tone: 'success' })

      if (isNew) {
        await navigate(`/catalog/products/${product.id}`, { replace: true })
      }
    },
    onError: (error) => {
      if (isProblemError(error)) {
        // A duplicate SKU is a 409 with no `errors` map — the body is
        // well-formed and would be accepted tomorrow if the other product were
        // renamed. Branch on `type` and put it on the field it is about.
        if (error.is(ErrorType.duplicateSku)) {
          setErrors({ sku: error.problem.detail ?? 'That SKU is already in use.' })
          return
        }

        const fieldErrors = error.fieldErrors

        if (Object.keys(fieldErrors).length > 0) {
          setErrors((current) => mergeFieldErrors(current, fieldErrors))
          return
        }
      }

      toast.showError(error, 'Could not save the product.')
    },
  })

  const setActive = useMutation({
    mutationFn: (active: boolean) =>
      unwrap(
        active
          ? api.POST('/api/v1/products/{id}/activate', { params: { path: { id: productId } } })
          : api.POST('/api/v1/products/{id}/deactivate', { params: { path: { id: productId } } }),
      ),
    onSuccess: async (_result, active) => {
      await queryClient.invalidateQueries({ queryKey: ['products'] })
      await queryClient.invalidateQueries({ queryKey: catalogKeys.product(productId) })
      toast.show(active ? 'Product is on sale again.' : 'Product withdrawn from sale.', {
        tone: 'success',
      })
    },
    onError: (error) => {
      toast.showError(error, 'Could not change the product.')
    },
  })

  function onSubmit(event: FormEvent) {
    event.preventDefault()

    const found = validateProduct(values)
    setErrors(found)

    if (Object.keys(found).length === 0) {
      save.mutate()
    }
  }

  if (!isNew && existing.isPending) {
    return <LoadingState label="Loading the product…" />
  }

  if (!isNew && existing.isError) {
    return (
      <ErrorState
        error={existing.error}
        onRetry={() => {
          void existing.refetch()
        }}
        title="Could not load that product."
      />
    )
  }

  return (
    <div className="mx-auto flex max-w-2xl flex-col gap-8">
      <header className="flex items-center justify-between gap-4">
        <div>
          <Link
            to="/catalog"
            className="text-sm text-muted-foreground underline underline-offset-4"
          >
            ← All products
          </Link>
          <h1 className="mt-1 text-xl font-semibold text-foreground">
            {isNew ? 'New product' : values.name}
          </h1>
        </div>

        {!isNew && existing.data !== undefined ? (
          existing.data.isActive ? (
            // No DELETE exists and there will not be one: sale lines reference
            // products forever, so a delete would either orphan history or
            // cascade a customer's sales away.
            <ConfirmButton
              confirmLabel="Really withdraw?"
              disabled={setActive.isPending}
              onConfirm={() => {
                setActive.mutate(false)
              }}
            >
              Withdraw from sale
            </ConfirmButton>
          ) : (
            <Button
              variant="outline"
              disabled={setActive.isPending}
              onClick={() => {
                setActive.mutate(true)
              }}
            >
              Sell again
            </Button>
          )
        ) : null}
      </header>

      <form onSubmit={onSubmit} className="flex flex-col gap-4">
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label="SKU" required error={errors['sku']}>
            {(fieldProps) => (
              <Input
                {...fieldProps}
                name="sku"
                className="font-mono"
                value={values.sku}
                onChange={(event) => {
                  setValues((current) => ({ ...current, sku: event.target.value }))
                }}
              />
            )}
          </Field>

          <Field label="Name" required error={errors['name']}>
            {(fieldProps) => (
              <Input
                {...fieldProps}
                name="name"
                value={values.name}
                onChange={(event) => {
                  setValues((current) => ({ ...current, name: event.target.value }))
                }}
              />
            )}
          </Field>
        </div>

        <Field label="Description" error={errors['description']}>
          {(fieldProps) => (
            <Textarea
              {...fieldProps}
              name="description"
              value={values.description}
              onChange={(event) => {
                setValues((current) => ({ ...current, description: event.target.value }))
              }}
            />
          )}
        </Field>

        <div className="grid gap-4 sm:grid-cols-2">
          <Field label="Category" error={errors['categoryId']}>
            {(fieldProps) => (
              <Select
                {...fieldProps}
                name="categoryId"
                value={values.categoryId}
                onChange={(event) => {
                  setValues((current) => ({ ...current, categoryId: event.target.value }))
                }}
              >
                <option value="">Uncategorised</option>
                {(categories.data ?? []).map((category) => (
                  <option key={category.id} value={category.id}>
                    {category.name}
                  </option>
                ))}
              </Select>
            )}
          </Field>

          <Field label="Tax class" required error={errors['taxClassId']}>
            {(fieldProps) => (
              <Select
                {...fieldProps}
                name="taxClassId"
                value={values.taxClassId}
                onChange={(event) => {
                  setValues((current) => ({ ...current, taxClassId: event.target.value }))
                }}
              >
                <option value="">Choose a tax class</option>
                {(taxClasses.data ?? []).map((taxClass) => (
                  <option key={taxClass.id} value={taxClass.id}>
                    {taxClass.name}
                  </option>
                ))}
              </Select>
            )}
          </Field>
        </div>

        <div className="grid gap-4 sm:grid-cols-2">
          <Field
            label="Unit price"
            required
            error={errors['unitPrice']}
            hint="The shelf price. The server does every calculation from it."
          >
            {(fieldProps) => (
              <Input
                {...fieldProps}
                name="unitPrice"
                inputMode="decimal"
                className="tabular-nums"
                value={values.unitPrice}
                onChange={(event) => {
                  setValues((current) => ({ ...current, unitPrice: event.target.value }))
                }}
              />
            )}
          </Field>

          {/*
            Margins are owner-only. This gate is defence in depth, not the
            control: for a caller without CanViewMargins the server's SQL never
            names cost_price and the field is omitted from the JSON entirely.
          */}
          <IfPolicy policy="CanViewMargins">
            <Field label="Cost price" error={errors['costPrice']} hint="Only owners see this.">
              {(fieldProps) => (
                <Input
                  {...fieldProps}
                  name="costPrice"
                  inputMode="decimal"
                  className="tabular-nums"
                  value={values.costPrice}
                  onChange={(event) => {
                    setValues((current) => ({ ...current, costPrice: event.target.value }))
                  }}
                />
              )}
            </Field>
          </IfPolicy>
        </div>

        <div className="grid gap-4 sm:grid-cols-2">
          <Field label="Sold by">
            {(fieldProps) => (
              <Select
                {...fieldProps}
                name="unit"
                value={values.unit}
                onChange={(event) => {
                  setValues((current) => ({ ...current, unit: event.target.value }))
                }}
              >
                <option value="Each">Each</option>
                <option value="Kilogram">Kilogram</option>
                <option value="Litre">Litre</option>
              </Select>
            )}
          </Field>

          <label className="flex items-end gap-2 pb-2 text-sm text-foreground">
            <input
              type="checkbox"
              name="trackStock"
              className="mb-1 size-4"
              checked={values.trackStock}
              onChange={(event) => {
                setValues((current) => ({ ...current, trackStock: event.target.checked }))
              }}
            />
            Track stock for this product
          </label>
        </div>

        <div className="flex gap-2">
          <Button type="submit" size="lg" disabled={save.isPending || !can('CanManageCatalog')}>
            {save.isPending ? 'Saving…' : isNew ? 'Create product' : 'Save changes'}
          </Button>
        </div>
      </form>

      {isNew ? (
        <p className="text-sm text-muted-foreground">
          Barcodes can be added once the product exists.
        </p>
      ) : (
        <BarcodePanel productId={productId} />
      )}
    </div>
  )
}
