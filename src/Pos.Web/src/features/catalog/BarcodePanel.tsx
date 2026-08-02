import { useRef, useState, type FormEvent } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { api, unwrap } from '@/api/client'
import { ErrorType, isProblemError } from '@/api/problem'
import { Button } from '@/components/ui/button'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { ConfirmButton } from '@/components/ConfirmButton'
import { EmptyState, ErrorState, LoadingState } from '@/components/states'
import { useToast } from '@/components/toastContext'
import { catalogKeys, useBarcodes } from './queries'
import { validateBarcode } from './validation'

/**
 * A product's barcodes, with a scan-to-add field.
 *
 * **Scan-to-add rather than type-to-add** because a hand-typed barcode is a
 * reliable source of typos, and a barcode with one digit wrong does not scan at
 * all — it fails silently at the till weeks later, as "this item won't scan".
 * A scanner is a keyboard that types the code and presses Enter, so the field
 * refocuses itself after every submit and a whole shelf can be scanned in
 * sequence without touching the mouse.
 */
export function BarcodePanel({ productId }: { productId: string }) {
  const queryClient = useQueryClient()
  const toast = useToast()
  const inputRef = useRef<HTMLInputElement>(null)

  const [code, setCode] = useState('')
  const [error, setError] = useState<string | undefined>(undefined)

  const barcodes = useBarcodes(productId)

  const add = useMutation({
    mutationFn: (value: string) =>
      unwrap(
        api.POST('/api/v1/products/{id}/barcodes', {
          params: { path: { id: productId } },
          body: { code: value, isPrimary: false },
        }),
      ),
    onSuccess: async () => {
      setCode('')
      setError(undefined)
      await queryClient.invalidateQueries({ queryKey: catalogKeys.barcodes(productId) })
      // Straight back to the field, so the next scan lands here.
      inputRef.current?.focus()
    },
    onError: (caught) => {
      if (isProblemError(caught)) {
        if (caught.is(ErrorType.duplicateBarcode)) {
          // Deliberately does not name the other product — see the exception's
          // remarks. "It's on something else" is all a shop assistant needs.
          setError(caught.problem.detail ?? 'That code is already on another product.')
          return
        }

        const fieldError = caught.fieldError('code')

        if (fieldError !== undefined) {
          setError(fieldError)
          return
        }
      }

      toast.showError(caught, 'Could not add the barcode.')
    },
  })

  const remove = useMutation({
    mutationFn: (barcodeId: string) =>
      unwrap(
        api.DELETE('/api/v1/products/{id}/barcodes/{barcodeId}', {
          params: { path: { id: productId, barcodeId } },
        }),
      ),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: catalogKeys.barcodes(productId) })
      toast.show('Barcode removed.', { tone: 'success' })
    },
    onError: (caught) => {
      toast.showError(caught, 'Could not remove the barcode.')
    },
  })

  function onSubmit(event: FormEvent) {
    event.preventDefault()

    const found = validateBarcode(code)
    setError(found)

    if (found === undefined) {
      add.mutate(code.trim())
    }
  }

  return (
    <section className="flex flex-col gap-3">
      <div>
        <h2 className="text-lg font-semibold text-foreground">Barcodes</h2>
        <p className="mt-0.5 text-sm text-muted-foreground">
          A product can carry several — a multipack and a single often have different codes.
        </p>
      </div>

      <form onSubmit={onSubmit} className="flex items-end gap-2">
        <Field label="Scan or type a code" error={error} className="flex-1">
          {(fieldProps) => (
            <Input
              {...fieldProps}
              ref={inputRef}
              name="code"
              className="font-mono"
              autoComplete="off"
              placeholder="Point the scanner here"
              value={code}
              onChange={(event) => {
                setCode(event.target.value)
              }}
            />
          )}
        </Field>
        <Button type="submit" className="mb-0.5" disabled={add.isPending || code.trim() === ''}>
          {add.isPending ? 'Adding…' : 'Add'}
        </Button>
      </form>

      {barcodes.isPending ? (
        <LoadingState label="Loading codes…" />
      ) : barcodes.isError ? (
        <ErrorState
          error={barcodes.error}
          onRetry={() => {
            void barcodes.refetch()
          }}
          title="Could not load the barcodes."
        />
      ) : barcodes.data.length === 0 ? (
        <EmptyState
          title="No codes yet."
          description="Without one, this product has to be found by name at the till."
        />
      ) : (
        <ul className="flex flex-col gap-2">
          {barcodes.data.map((barcode) => (
            <li
              key={barcode.id}
              className="flex items-center justify-between rounded-lg border border-border bg-card px-3 py-2"
            >
              <span className="font-mono text-sm text-card-foreground">{barcode.code}</span>
              {/* A barcode may be deleted, unlike a product: a mis-scanned
                  label is data entry, not history. Nothing financial points at
                  one — a sale line points at the product. */}
              <ConfirmButton
                confirmLabel="Really remove?"
                disabled={remove.isPending}
                onConfirm={() => {
                  remove.mutate(barcode.id)
                }}
              >
                Remove
              </ConfirmButton>
            </li>
          ))}
        </ul>
      )}
    </section>
  )
}
