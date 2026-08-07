import type { components } from '@/api/schema'
import { formatMoney, parseServerDecimal } from '@/lib/money'
import type { SaleProvenance } from './queries'

type SaleResponse = components['schemas']['SaleResponse']

/**
 * What happened, once the money is in the drawer.
 *
 * **Change due is the largest thing on the screen** — larger than the total ever
 * was — because it is the number the cashier reads out loud while counting notes
 * back into a customer's hand. Getting it wrong is the mistake a shop notices at
 * cash-up and cannot attribute to anyone.
 *
 * It is the **server's** figure, from `TenderRules.ChangeFor` against the amounts
 * actually recorded on the sale. The running balance in the tender pad was
 * provisional and said so; this is not.
 *
 * There is no dismiss button. The cart is already clear and the next scan
 * replaces this panel, so the next customer costs no extra click — which is the
 * difference between a till that keeps up with a queue and one that does not.
 */
export function SaleCompletePanel({
  sale,
  currency,
  provenance,
}: {
  sale: SaleResponse
  currency: string
  /** How the till came to be holding this sale. See `SaleProvenance`. */
  provenance: SaleProvenance
}) {
  const change = parseServerDecimal(sale.changeGiven)
  const rounding = parseServerDecimal(sale.roundingAdjustment)

  return (
    <section
      aria-label="Sale complete"
      data-testid="sale-complete"
      className="flex flex-col gap-3 rounded-xl border border-primary/30 bg-primary/5 p-4"
    >
      <div className="flex items-baseline justify-between gap-3">
        <span className="text-sm font-semibold text-foreground">
          {change > 0 ? 'Change due' : 'Paid in full'}
        </span>
        <span className="text-xs text-muted-foreground">
          Sale #{String(sale.saleNumber ?? '—')}
        </span>
      </div>

      <p
        data-testid="change-due"
        className="text-6xl leading-none font-semibold tabular-nums text-foreground"
      >
        {formatMoney(sale.changeGiven, currency)}
      </p>

      {/* The visible half of invariant 6, in the two shapes it takes. A cashier
          who pressed twice or retried after a dropped connection needs to know
          the second attempt took no second payment; one who reloaded needs to
          know the payment happened at all, because they did not see it. */}
      {provenance === 'replayed' ? (
        <p
          data-testid="sale-replayed"
          role="status"
          className="rounded-md bg-muted px-3 py-2 text-sm text-foreground"
        >
          <span className="font-medium">Already recorded.</span> This sale had gone through — it has
          not been charged again.
        </p>
      ) : provenance === 'recovered' ? (
        <p
          data-testid="sale-recovered"
          role="status"
          className="rounded-md bg-muted px-3 py-2 text-sm text-foreground"
        >
          <span className="font-medium">Taken before this page reloaded.</span> The payment went
          through and was charged once. Nothing else is owed.
        </p>
      ) : null}

      <dl className="grid grid-cols-2 gap-x-3 gap-y-1 text-sm">
        <dt className="text-muted-foreground">Total</dt>
        <dd className="text-right tabular-nums text-foreground">
          {formatMoney(sale.total, currency)}
        </dd>

        {/* Every row labelled, not just the first. A split tender listed as
            "Cash taken €10.00" followed by two bare amounts reads as a broken
            list, and this is the panel a cashier checks when the drawer does not
            balance. */}
        {sale.tenders.map((tender, index) => (
          <Row
            key={index}
            label={sale.tenders.length === 1 ? 'Cash taken' : `Cash ${String(index + 1)}`}
            value={formatMoney(tender.amount, currency)}
          />
        ))}

        {rounding !== 0 ? (
          <Row label="Cash rounding" value={formatMoney(sale.roundingAdjustment, currency)} />
        ) : null}
      </dl>

      <p className="text-xs text-muted-foreground">
        {/* Honest about what is not here yet, as `Take cash` was before this
            milestone built it. A stub that pretended to print would be worse. */}
        Scan the next item to start a new sale. Printed receipts arrive in a later milestone; the
        sale is in the history meanwhile.
      </p>
    </section>
  )
}

function Row({ label, value }: { label: string; value: string }) {
  return (
    <>
      <dt className="text-muted-foreground">{label}</dt>
      <dd className="text-right tabular-nums text-foreground">{value}</dd>
    </>
  )
}
