import { useState } from 'react'
import { Link, useParams } from 'react-router'
import { useAuth } from '@/auth/authContext'
import { Button } from '@/components/ui/button'
import { ErrorState, LoadingState } from '@/components/states'
import { formatMoney, formatQuantity, formatRate, parseServerDecimal } from '@/lib/money'
import { ReceiptDialog } from './ReceiptDialog'
import { RefundDialog } from './RefundDialog'
import { useSale } from './queries'

/**
 * One sale, in full.
 *
 * Everything here is the sale's own snapshot — the description, the unit price
 * and the tax rate as they were when it was rung. Nothing joins to the catalog,
 * which is the point: this is the screen somebody opens when a customer disputes
 * what they were charged, and the answer has to be what was charged.
 */
export function SaleDetailPage() {
  const { saleId = null } = useParams<{ saleId: string }>()
  const { tenant, can } = useAuth()
  const currency = tenant?.currencyCode ?? 'GBP'

  const sale = useSale(saleId)

  const [printing, setPrinting] = useState(false)
  const [refunding, setRefunding] = useState(false)

  if (sale.isPending) {
    return <LoadingState label="Fetching the sale…" />
  }

  if (sale.isError) {
    return (
      <div className="p-4">
        <ErrorState
          error={sale.error}
          title="That sale could not be loaded."
          onRetry={() => {
            void sale.refetch()
          }}
        />
      </div>
    )
  }

  const data = sale.data
  const voided = data.status === 'Voided'
  const isRefund = data.type === 'Refund'
  const refunds = data.refunds ?? []

  return (
    <div className="flex flex-col gap-4 p-4">
      <header className="flex flex-wrap items-start justify-between gap-4">
        <div>
          <h1 className="text-lg font-semibold text-foreground">
            Sale #{String(data.saleNumber ?? '—')}
            {isRefund ? ' · Refund' : ''}
          </h1>
          <p className="text-sm text-muted-foreground">
            {data.completedAt === null ? '' : formatStamp(data.completedAt)} · {data.cashierName} ·{' '}
            {data.registerName}
          </p>
        </div>

        <div className="flex flex-wrap gap-2">
          <Button
            variant="outline"
            size="sm"
            onClick={() => {
              setPrinting(true)
            }}
          >
            Reprint receipt
          </Button>

          {/* Gated on CanRefund, and the server re-checks. A refund of a refund
              is not a thing, and a voided sale has already been reversed. */}
          {can('CanRefund') && !voided && !isRefund ? (
            <Button
              size="sm"
              onClick={() => {
                setRefunding(true)
              }}
            >
              Refund
            </Button>
          ) : null}
        </div>
      </header>

      {voided ? (
        <p
          role="status"
          data-testid="sale-voided"
          className="rounded-lg border border-destructive/40 bg-destructive/10 px-4 py-2.5 text-sm"
        >
          <span className="font-medium">This sale was voided.</span> {data.voidReason ?? ''}
          {data.voidedByName === null ? '' : ` — ${data.voidedByName}`}
        </p>
      ) : null}

      {/* Both halves of the link. Forwards from a refund to what it reverses,
          and backwards from a sale to what has already been given back — without
          the second, the same receipt gets refunded twice. */}
      {data.originalSaleId !== null ? (
        <p data-testid="sale-original" className="text-sm text-muted-foreground">
          Refunds sale{' '}
          <Link to={`/sales/${data.originalSaleId}`} className="text-foreground underline">
            #{String(data.originalSaleNumber ?? '—')}
          </Link>
          {data.refundReason === null ? '' : ` — ${data.refundReason}`}
        </p>
      ) : null}

      {refunds.length > 0 ? (
        <p data-testid="sale-refunds" className="text-sm text-muted-foreground">
          <span className="font-medium text-foreground">Already refunded:</span>{' '}
          {refunds.map((refund, index) => (
            <span key={refund.id}>
              {index > 0 ? ', ' : ''}
              <Link to={`/sales/${refund.id}`} className="text-foreground underline">
                #{String(refund.saleNumber)}
              </Link>{' '}
              ({formatMoney(refund.total, currency)})
            </span>
          ))}
        </p>
      ) : null}

      <section className="overflow-x-auto rounded-xl border border-border">
        <table className="w-full text-sm">
          <thead>
            <tr className="border-b border-border">
              <th className="px-3 py-2 text-left font-medium text-muted-foreground">Item</th>
              <th className="px-3 py-2 text-right font-medium text-muted-foreground">Qty</th>
              <th className="px-3 py-2 text-right font-medium text-muted-foreground">Price</th>
              <th className="px-3 py-2 text-right font-medium text-muted-foreground">VAT</th>
              <th className="px-3 py-2 text-right font-medium text-muted-foreground">Total</th>
            </tr>
          </thead>
          <tbody>
            {data.lines.map((line) => (
              <tr key={line.id} data-testid="sale-line" className="border-b border-border/60">
                <td className="px-3 py-2 text-foreground">
                  {line.description}
                  {line.isPriceOverridden ? (
                    <span className="ml-2 text-xs text-muted-foreground">price changed</span>
                  ) : null}
                  {parseServerDecimal(line.discountAmount) !== 0 ? (
                    <span className="ml-2 text-xs text-muted-foreground">
                      less {formatMoney(line.discountAmount, currency)}
                    </span>
                  ) : null}
                </td>
                <td className="px-3 py-2 text-right tabular-nums">
                  {formatQuantity(line.quantity)}
                </td>
                <td className="px-3 py-2 text-right tabular-nums">
                  {formatMoney(line.unitPrice, currency)}
                </td>
                <td className="px-3 py-2 text-right tabular-nums">{formatRate(line.taxRate)}</td>
                <td className="px-3 py-2 text-right tabular-nums">
                  {formatMoney(line.lineTotal, currency)}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </section>

      <section className="flex flex-col gap-1 rounded-xl border border-border p-4 text-sm">
        <Row label="Subtotal" value={formatMoney(data.subtotal, currency)} />
        {parseServerDecimal(data.discountTotal) !== 0 ? (
          <Row label="Discounts" value={formatMoney(data.discountTotal, currency)} />
        ) : null}
        <Row label="Tax" value={formatMoney(data.taxTotal, currency)} />
        {parseServerDecimal(data.roundingAdjustment) !== 0 ? (
          <Row label="Cash rounding" value={formatMoney(data.roundingAdjustment, currency)} />
        ) : null}
        <Row label="Total" value={formatMoney(data.total, currency)} strong />

        {data.tenders.map((tender, index) => (
          <Row
            key={index}
            label={
              data.tenders.length === 1 ? tender.method : `${tender.method} ${String(index + 1)}`
            }
            value={formatMoney(tender.amount, currency)}
          />
        ))}

        {parseServerDecimal(data.changeGiven) !== 0 ? (
          <Row label="Change" value={formatMoney(data.changeGiven, currency)} />
        ) : null}
      </section>

      {/* Always a reprint from here: the original was the copy handed over at
          the counter when the sale was rung. */}
      {printing && data.id !== null ? (
        <ReceiptDialog
          saleId={data.id}
          isReprint
          onClose={() => {
            setPrinting(false)
          }}
        />
      ) : null}

      {refunding ? (
        <RefundDialog
          sale={data}
          currency={currency}
          onClose={() => {
            setRefunding(false)
          }}
          onRefunded={() => {
            setRefunding(false)
          }}
        />
      ) : null}
    </div>
  )
}

function Row({ label, value, strong = false }: { label: string; value: string; strong?: boolean }) {
  return (
    <div className="flex justify-between gap-4 border-b border-border/40 py-1 last:border-0">
      <span className="text-muted-foreground">{label}</span>
      <span className={strong ? 'font-semibold tabular-nums' : 'tabular-nums'}>{value}</span>
    </div>
  )
}

function formatStamp(value: string): string {
  return new Intl.DateTimeFormat(undefined, {
    dateStyle: 'medium',
    timeStyle: 'short',
  }).format(new Date(value))
}
