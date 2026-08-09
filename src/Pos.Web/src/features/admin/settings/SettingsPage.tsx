import { useEffect, useState, type FormEvent } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, unwrap } from '@/api/client'
import { isProblemError } from '@/api/problem'
import { Button } from '@/components/ui/button'
import { Field } from '@/components/ui/field'
import { Input, Select, Textarea } from '@/components/ui/input'
import { ErrorState, LoadingState } from '@/components/states'
import { useToast } from '@/components/toastContext'
import { mergeFieldErrors, type FieldErrors } from '@/features/catalog/validation'

/**
 * What a shop can change about itself.
 *
 * Until Phase 7 these were reachable only through `tools/Pos.Seed`, which is a
 * developer tool that is not shipped — so a customer could not change their own
 * receipt footer at all.
 *
 * Currency, time zone and the trading-day offset are shown and not editable. They
 * are not oversights: changing a zone or a day-start offset moves every trading-day
 * boundary that has already been reported on, so yesterday's Z-report stops matching
 * yesterday. Those are migrations with a decision behind them.
 */
export function SettingsPage() {
  const queryClient = useQueryClient()
  const toast = useToast()

  const settings = useQuery({
    queryKey: ['settings'],
    queryFn: () => unwrap(api.GET('/api/v1/settings')),
  })

  const [name, setName] = useState('')
  const [taxMode, setTaxMode] = useState<'Inclusive' | 'Exclusive'>('Inclusive')
  const [rounding, setRounding] = useState('')
  const [addressLine, setAddressLine] = useState('')
  const [taxNumber, setTaxNumber] = useState('')
  const [receiptHeader, setReceiptHeader] = useState('')
  const [receiptFooter, setReceiptFooter] = useState('')
  const [errors, setErrors] = useState<FieldErrors>({})
  const [loaded, setLoaded] = useState(false)

  // Seeded once, on the same `loaded` flag `ProductDetailPage` uses rather than a `key`
  // remount — a refetch mid-edit must not discard what somebody has typed.
  useEffect(() => {
    if (loaded || settings.data === undefined) {
      return
    }

    setName(settings.data.name)

    // The generated type is nullable because .NET's OpenAPI describes every enum that
    // way; the server always sends one. Defaulted rather than asserted, so an
    // unexpected null renders a form instead of throwing.
    setTaxMode(settings.data.taxMode ?? 'Inclusive')
    setRounding(String(settings.data.cashRoundingIncrement))
    setAddressLine(settings.data.addressLine ?? '')
    setTaxNumber(settings.data.taxNumber ?? '')
    setReceiptHeader(settings.data.receiptHeader ?? '')
    setReceiptFooter(settings.data.receiptFooter ?? '')
    setLoaded(true)
  }, [loaded, settings.data])

  const save = useMutation({
    mutationFn: () =>
      unwrap(
        api.PUT('/api/v1/settings', {
          body: {
            name: name.trim(),
            taxMode,
            cashRoundingIncrement: Number(rounding),
            addressLine: blank(addressLine),
            taxNumber: blank(taxNumber),
            receiptHeader: blank(receiptHeader),
            receiptFooter: blank(receiptFooter),
          },
        }),
      ),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['settings'] })

      // `/auth/me` carries a subset of these for the app shell, so it is stale now too.
      await queryClient.invalidateQueries({ queryKey: ['auth', 'me'] })
      toast.show('Saved.', { tone: 'success' })
    },
    onError: (caught) => {
      if (isProblemError(caught)) {
        const fieldErrors = caught.fieldErrors

        if (Object.keys(fieldErrors).length > 0) {
          setErrors((current) => mergeFieldErrors(current, fieldErrors))
          return
        }
      }

      toast.showError(caught, 'Could not save the settings.')
    },
  })

  if (settings.isPending) {
    return <LoadingState label="Loading the settings…" />
  }

  if (settings.isError) {
    return (
      <ErrorState
        error={settings.error}
        onRetry={() => {
          void settings.refetch()
        }}
        title="Could not load the settings."
      />
    )
  }

  function onSubmit(event: FormEvent) {
    event.preventDefault()

    const found: FieldErrors = {}

    if (name.trim() === '') {
      found['name'] = 'A shop name is required.'
    }

    // Mirrors the server's rule. An increment of 5 rounds every cash total to the
    // nearest fiver, which is a typo for 0.05 rather than a rule anybody has.
    const increment = Number(rounding)

    if (rounding.trim() === '' || Number.isNaN(increment) || increment < 0 || increment > 1) {
      found['cashRoundingIncrement'] = 'A value between 0 and 1, such as 0.05.'
    }

    setErrors(found)

    if (Object.keys(found).length === 0) {
      save.mutate()
    }
  }

  const locked = settings.data.taxModeLocked

  return (
    <form onSubmit={onSubmit} className="mx-auto flex max-w-2xl flex-col gap-8 p-6">
      <header>
        <h1 className="text-xl font-semibold text-foreground">Shop settings</h1>
      </header>

      <section className="flex flex-col gap-4">
        <h2 className="text-sm font-semibold text-foreground">The shop</h2>

        <Field label="Name" required error={errors['name']}>
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

        <div className="grid gap-4 sm:grid-cols-2">
          <Field label="Currency" hint="Fixed after onboarding.">
            {(fieldProps) => <Input {...fieldProps} value={settings.data.currencyCode} disabled />}
          </Field>

          <Field label="Time zone" hint="Changing it would move every past trading day.">
            {(fieldProps) => <Input {...fieldProps} value={settings.data.timeZoneId} disabled />}
          </Field>
        </div>
      </section>

      <section className="flex flex-col gap-4">
        <h2 className="text-sm font-semibold text-foreground">Money</h2>

        <Field
          label="Tax mode"
          hint={
            locked
              ? 'Fixed — this shop has recorded a sale. Changing it would reinterpret every price already stored.'
              : 'Whether your prices already include tax.'
          }
        >
          {(fieldProps) => (
            <Select
              {...fieldProps}
              value={taxMode}
              // Read-only with a reason, rather than a control that submits and 409s.
              // The server refuses it either way; this explains why before the click.
              disabled={locked}
              onChange={(event) => {
                setTaxMode(event.target.value as 'Inclusive' | 'Exclusive')
              }}
            >
              <option value="Inclusive">Prices include tax</option>
              <option value="Exclusive">Tax added at the till</option>
            </Select>
          )}
        </Field>

        <Field
          label="Cash rounding"
          error={errors['cashRoundingIncrement']}
          hint="The smallest coin you round a cash total to. 0 means no rounding."
        >
          {(fieldProps) => (
            <Input
              {...fieldProps}
              inputMode="decimal"
              className="max-w-32 tabular-nums"
              value={rounding}
              onChange={(event) => {
                setRounding(event.target.value)
              }}
            />
          )}
        </Field>
      </section>

      <section className="flex flex-col gap-4">
        <h2 className="text-sm font-semibold text-foreground">Receipts</h2>

        <Field label="Address" error={errors['addressLine']} hint="Printed under the shop name.">
          {(fieldProps) => (
            <Textarea
              {...fieldProps}
              rows={3}
              value={addressLine}
              onChange={(event) => {
                setAddressLine(event.target.value)
              }}
            />
          )}
        </Field>

        <Field label="Tax number" error={errors['taxNumber']}>
          {(fieldProps) => (
            <Input
              {...fieldProps}
              value={taxNumber}
              onChange={(event) => {
                setTaxNumber(event.target.value)
              }}
            />
          )}
        </Field>

        <Field label="Header" error={errors['receiptHeader']} hint="Above the items.">
          {(fieldProps) => (
            <Textarea
              {...fieldProps}
              rows={2}
              value={receiptHeader}
              onChange={(event) => {
                setReceiptHeader(event.target.value)
              }}
            />
          )}
        </Field>

        <Field
          label="Footer"
          error={errors['receiptFooter']}
          hint="Below the total — a returns policy, or a thank you."
        >
          {(fieldProps) => (
            <Textarea
              {...fieldProps}
              rows={3}
              value={receiptFooter}
              onChange={(event) => {
                setReceiptFooter(event.target.value)
              }}
            />
          )}
        </Field>
      </section>

      <Button type="submit" className="self-start" disabled={save.isPending}>
        {save.isPending ? 'Saving…' : 'Save settings'}
      </Button>
    </form>
  )
}

function blank(value: string): string | null {
  return value.trim() === '' ? null : value.trim()
}
