import { useState } from 'react'
import { Link } from 'react-router'
import { Button } from '@/components/ui/button'
import { EmptyState, ErrorState, LoadingState } from '@/components/states'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { formatMoney } from '@/lib/money'
import { cn } from '@/lib/utils'
import { EMPTY_FILTERS, useSaleHistory, type SaleFilters } from './queries'
import { useAuth } from '@/auth/authContext'

/**
 * The sale history.
 *
 * **Search by sale number is the prominent control**, not one filter among six.
 * It is the reference a customer reads off their receipt, and the counter is
 * where this screen is actually used — somebody standing there with a bag and a
 * piece of paper. The date, register and type filters are for the office.
 */
export function SaleListPage() {
  const { tenant } = useAuth()
  const currency = tenant?.currencyCode ?? 'GBP'

  const [filters, setFilters] = useState<SaleFilters>(EMPTY_FILTERS)

  const history = useSaleHistory(filters)
  const rows = history.data?.pages.flatMap((page) => page.items) ?? []

  const set = (patch: Partial<SaleFilters>) => {
    setFilters((current) => ({ ...current, ...patch }))
  }

  const filtered = Object.values(filters).some((value) => value !== '')

  return (
    <div className="flex flex-col gap-4 p-4">
      <header className="flex flex-wrap items-end justify-between gap-4">
        <h1 className="text-lg font-semibold text-foreground">Sales</h1>

        <Field label="Sale number" hint="The number on the customer's receipt">
          {(props) => (
            <Input
              {...props}
              inputMode="numeric"
              placeholder="e.g. 1043"
              className="w-48"
              value={filters.saleNumber}
              onChange={(event) => {
                // Digits only: the field is a lookup, and a stray character
                // would be sent as a filter the server rejects.
                set({ saleNumber: event.target.value.replaceAll(/\D/g, '') })
              }}
            />
          )}
        </Field>
      </header>

      <div className="flex flex-wrap items-end gap-3 rounded-xl border border-border p-3">
        <Field label="From">
          {(props) => (
            <Input
              {...props}
              type="date"
              value={filters.from}
              onChange={(event) => {
                set({ from: event.target.value })
              }}
            />
          )}
        </Field>

        <Field label="To">
          {(props) => (
            <Input
              {...props}
              type="date"
              value={filters.to}
              onChange={(event) => {
                set({ to: event.target.value })
              }}
            />
          )}
        </Field>

        <Field label="Type">
          {(props) => (
            <Select
              {...props}
              value={filters.type}
              options={['Sale', 'Refund']}
              onChange={(value) => {
                set({ type: value })
              }}
            />
          )}
        </Field>

        <Field label="Status">
          {(props) => (
            <Select
              {...props}
              value={filters.status}
              options={['Completed', 'Voided']}
              onChange={(value) => {
                set({ status: value })
              }}
            />
          )}
        </Field>

        {filtered ? (
          <Button
            variant="ghost"
            size="sm"
            onClick={() => {
              setFilters(EMPTY_FILTERS)
            }}
          >
            Clear
          </Button>
        ) : null}
      </div>

      {history.isPending ? (
        <LoadingState label="Fetching the history…" />
      ) : history.isError ? (
        <ErrorState
          error={history.error}
          title="The history could not be loaded."
          onRetry={() => {
            void history.refetch()
          }}
        />
      ) : rows.length === 0 ? (
        <EmptyState
          // A sale number that finds nothing is a different fact from a filter
          // that matches nothing, and the person at the counter needs the first.
          title={
            filters.saleNumber !== ''
              ? `No sale numbered ${filters.saleNumber}.`
              : filtered
                ? 'Nothing matched.'
                : 'Nothing has been sold yet.'
          }
          description={
            filters.saleNumber !== ''
              ? 'Check the number on the receipt — it may belong to another shop.'
              : undefined
          }
        />
      ) : (
        <>
          <div className="overflow-x-auto rounded-xl border border-border">
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b border-border">
                  <Th>Sale</Th>
                  <Th>When</Th>
                  <Th>Till</Th>
                  <Th>Cashier</Th>
                  <Th>Type</Th>
                  <Th align="right">Total</Th>
                </tr>
              </thead>
              <tbody>
                {rows.map((sale) => (
                  <tr key={sale.id} data-testid="sale-row" className="border-b border-border/60">
                    <Td>
                      <Link
                        to={`/sales/${sale.id}`}
                        className="font-medium text-foreground underline-offset-2 hover:underline"
                      >
                        #{String(sale.saleNumber)}
                      </Link>
                    </Td>
                    <Td>{formatStamp(sale.completedAt)}</Td>
                    <Td>{sale.registerName}</Td>
                    <Td>{sale.cashierName}</Td>
                    <Td>
                      {/* A voided sale is not hidden from the history — a
                          manager who cannot find a transaction concludes the
                          system lost it — but it must be unmistakable. */}
                      <span
                        className={cn(
                          sale.status === 'Voided' && 'font-medium text-destructive',
                        )}
                      >
                        {sale.status === 'Voided' ? 'Voided' : sale.type}
                      </span>
                    </Td>
                    <Td align="right">{formatMoney(sale.total, currency)}</Td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>

          {history.hasNextPage ? (
            <Button
              variant="outline"
              className="self-start"
              disabled={history.isFetchingNextPage}
              onClick={() => {
                void history.fetchNextPage()
              }}
            >
              {history.isFetchingNextPage ? 'Loading…' : 'Show more'}
            </Button>
          ) : null}
        </>
      )}
    </div>
  )
}

/**
 * A plain select. Empty option first, because "any" is the default and has to be
 * reachable again after a choice.
 */
function Select({
  value,
  options,
  onChange,
  ...props
}: {
  value: string
  options: string[]
  onChange: (value: string) => void
  id: string
  'aria-invalid': boolean
  'aria-describedby': string | undefined
}) {
  return (
    <select
      {...props}
      value={value}
      onChange={(event) => {
        onChange(event.target.value)
      }}
      className="h-9 rounded-md border border-input bg-transparent px-3 text-sm text-foreground"
    >
      <option value="">Any</option>
      {options.map((option) => (
        <option key={option} value={option}>
          {option}
        </option>
      ))}
    </select>
  )
}

function Th({ children, align = 'left' }: { children: string; align?: 'left' | 'right' }) {
  return (
    <th
      className={cn(
        'px-3 py-2 font-medium text-muted-foreground',
        align === 'right' ? 'text-right' : 'text-left',
      )}
    >
      {children}
    </th>
  )
}

function Td({
  children,
  align = 'left',
}: {
  children: React.ReactNode
  align?: 'left' | 'right'
}) {
  return (
    <td
      className={cn(
        'px-3 py-2 text-foreground',
        align === 'right' && 'text-right tabular-nums',
      )}
    >
      {children}
    </td>
  )
}

/** UTC on the wire; the browser's own zone on a back-office list. */
function formatStamp(value: string): string {
  return new Intl.DateTimeFormat(undefined, {
    day: '2-digit',
    month: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
  }).format(new Date(value))
}
