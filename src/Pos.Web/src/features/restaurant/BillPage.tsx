import { useMemo, useState } from 'react'
import { useNavigate, useParams } from 'react-router'
import { ErrorType, isErrorType, isProblemError } from '@/api/problem'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { ErrorState, LoadingState } from '@/components/states'
import { useToast } from '@/components/toastContext'
import { getEnrolledRegisterId } from '@/auth/deviceToken'
import { useCurrentShift } from '@/features/register/queries'
import { TenderPanel } from '@/features/register/TenderPanel'
import { quickCash, tenderedMinor, type Tender } from '@/features/register/tender'
import { formatMoney, toMinorUnits } from '@/lib/money'
import { useOperationKey } from './idempotency'
import { useBills, useCreateBill, useOrder, usePayBill, type OrderBill } from './queries'

/**
 * Settling a table.
 *
 * **The amounts here are a quote until the bill is paid.** Nothing is stored:
 * the endpoint prices the allocated lines through the same engine the payment
 * will use, so re-reading after a line is voided returns different numbers,
 * correctly. That is also why this screen never adds anything up itself.
 *
 * **An even split is N tenders on one bill, not N bills.** `Tender` has been a
 * collection since Phase 3 and the running balance already exists, so four
 * people paying a quarter each is four cash amounts against one sale — no new
 * mechanism and no fractional-quantity rounding. Splitting *by item or by seat*
 * is what needs more than one bill, and there the allocations must sum exactly
 * to each line's quantity.
 */
export function BillPage() {
  const { orderId = null } = useParams<{ orderId: string }>()
  const navigate = useNavigate()

  const order = useOrder(orderId)
  const bills = useBills(orderId)
  const key = useOperationKey()
  const create = useCreateBill(orderId ?? '', key.key)
  const toast = useToast()

  if (order.isPending || bills.isPending) {
    return <LoadingState label="Pricing the table…" />
  }

  if (bills.isError) {
    return (
      <ErrorState title="Could not price the bill." error={bills.error} onRetry={bills.refetch} />
    )
  }

  const open = (bills.data ?? []).filter((bill) => bill.status !== 'Paid')
  const paid = (bills.data ?? []).filter((bill) => bill.status === 'Paid')

  const wholeTable = async () => {
    try {
      // No allocations: everything still unbilled, which is the ordinary case.
      await create.mutateAsync(null)
      key.renew()
    } catch (caught) {
      toast.showError(caught, 'Could not open a bill.')
    }
  }

  return (
    <div className="flex flex-col gap-4 p-4">
      <header className="flex flex-wrap items-baseline justify-between gap-3">
        <h1 className="text-lg font-semibold text-foreground">
          Bill · order #{order.data?.orderNumber ?? '—'}
        </h1>

        <Button
          type="button"
          variant="outline"
          onClick={() => void navigate(`/restaurant/orders/${orderId!}`)}
        >
          Back to the order
        </Button>
      </header>

      {open.length === 0 && paid.length === 0 ? (
        <div className="flex flex-col items-start gap-3 rounded-xl border border-dashed border-border p-4">
          <p className="text-sm text-muted-foreground">
            Nothing is billed yet. Take the whole table, or split it by item on the order screen.
          </p>
          <Button type="button" onClick={() => void wholeTable()} disabled={create.isPending}>
            Bill the whole table
          </Button>
        </div>
      ) : null}

      {open.map((bill) => (
        <BillCard key={bill.id} orderId={orderId!} bill={bill} />
      ))}

      {paid.map((bill) => (
        <PaidCard key={bill.id} bill={bill} />
      ))}

      {open.length === 0 && paid.length > 0 ? (
        <Button type="button" onClick={() => void navigate('/register')}>
          Done — back to the floor
        </Button>
      ) : null}
    </div>
  )
}

/** One unpaid bill, and the pad that settles it. */
function BillCard({ orderId, bill }: { orderId: string; bill: OrderBill }) {
  const toast = useToast()
  const shift = useCurrentShift()
  const registerId = getEnrolledRegisterId()

  // Minted with the bill and held across every attempt (invariant 6). A key per
  // press would make the header decorative and charge the table twice.
  const key = useOperationKey()
  const pay = usePayBill(orderId, key.key)

  const [tenders, setTenders] = useState<Tender[]>([])
  const [pending, setPending] = useState('')
  const [tip, setTip] = useState('')

  const currency = 'EUR'
  const totalMinor = toMinorUnits(bill.total)
  const tipMinor = tip === '' ? 0 : Math.round(Number(tip) * 100)

  const covered = useMemo(
    () => tenderedMinor(tenders) >= totalMinor + tipMinor,
    [tenders, totalMinor, tipMinor],
  )

  const settle = async () => {
    if (shift.data === undefined || registerId === null) {
      toast.show('This till has no open drawer, so it cannot take the money.', { tone: 'error' })
      return
    }

    try {
      await pay.mutateAsync({
        billId: bill.id,
        registerId,
        shiftId: shift.data.id,
        tenders: tenders.map((tender) => ({ amount: tender.amountMinor / 100 })),
        tip: tip === '' ? null : Number(tip),
      })
    } catch (caught) {
      if (isErrorType(caught, ErrorType.underTender)) {
        toast.show('That does not cover the bill and the tip.', {
          tone: 'error',
          detail: isProblemError(caught) ? caught.problem.detail : undefined,
        })
        return
      }

      toast.showError(caught, 'Could not take that payment.')
    }
  }

  return (
    <section
      aria-label={`Bill ${bill.billNumber}`}
      data-testid={`bill-${bill.billNumber}`}
      className="flex flex-col gap-3 rounded-xl border border-border p-3"
    >
      <div className="flex items-baseline justify-between gap-3">
        <h2 className="text-sm font-semibold text-foreground">Bill {bill.billNumber}</h2>
        <span className="text-base font-semibold tabular-nums">
          {formatMoney(bill.total, currency)}
        </span>
      </div>

      <ul className="flex flex-col gap-0.5">
        {bill.lines.map((line) => (
          <li key={line.orderLineId} className="flex justify-between gap-3 text-sm">
            <span className="text-foreground">
              <span className="tabular-nums text-muted-foreground">{line.quantity}×</span>{' '}
              {line.description}
            </span>
            <span className="tabular-nums text-muted-foreground">
              {formatMoney(line.lineTotal, currency)}
            </span>
          </li>
        ))}
      </ul>

      <label className="flex w-32 flex-col gap-1 text-xs text-muted-foreground">
        Tip
        <Input
          type="number"
          min={0}
          step="0.01"
          value={tip}
          placeholder="0.00"
          onChange={(event) => setTip(event.target.value)}
        />
      </label>

      {/*
       * The retail cash pad, unchanged. Its `quote` prop was widened to the two
       * money fields it actually reads, so a bill satisfies it without being
       * dressed up as a sale — and there is exactly one place in the app where
       * the running balance is computed.
       */}
      <TenderPanel
        quote={bill}
        currency={currency}
        tenders={tenders}
        pending={pending}
        submitting={pay.isPending}
        onAdd={(amountMinor) => {
          setTenders((current) => [...current, { key: crypto.randomUUID(), amountMinor }])
          setPending('')
        }}
        onRemove={(removed) => setTenders((current) => current.filter((t) => t.key !== removed))}
        onComplete={() => void settle()}
        onCancel={() => setTenders([])}
      />

      {tenders.length === 0 ? (
        <div className="flex flex-wrap gap-2">
          {quickCash(totalMinor + tipMinor, currency).map((amountMinor) => (
            <Button
              key={amountMinor}
              type="button"
              variant="outline"
              size="sm"
              onClick={() => setTenders([{ key: crypto.randomUUID(), amountMinor }])}
            >
              {formatMoney(amountMinor / 100, currency)}
            </Button>
          ))}
        </div>
      ) : null}

      {covered ? null : (
        <p className="text-xs text-muted-foreground">
          Add cash until the bill and the tip are covered.
        </p>
      )}
    </section>
  )
}

/**
 * A settled bill.
 *
 * **Change due is the server's figure**, carried on the pay response rather than
 * fetched afterwards — see `OrderBillResponse.ChangeGiven`. It is the largest
 * thing here for the reason it is on the retail till: it is the number somebody
 * reads out loud while counting notes back into a hand.
 *
 * **The tip is shown beside it, not folded into the total**, because that is the
 * arithmetic the guest just did: ten over on a bill with a four-euro tip is six
 * back, and the four stays in the drawer.
 */
function PaidCard({ bill }: { bill: OrderBill }) {
  const currency = 'EUR'
  const change = bill.changeGiven

  return (
    <section
      aria-label={`Bill ${bill.billNumber} paid`}
      data-testid="bill-paid"
      className="flex flex-col gap-2 rounded-xl border border-primary/30 bg-primary/5 p-4"
    >
      <div className="flex items-baseline justify-between gap-3">
        <span className="text-sm font-semibold text-foreground">
          {change !== null && Number(change) > 0 ? 'Change due' : 'Paid in full'}
        </span>
        <span className="text-xs text-muted-foreground">Bill {bill.billNumber}</span>
      </div>

      {change !== null && Number(change) > 0 ? (
        <span className="text-4xl font-semibold tabular-nums text-foreground">
          {formatMoney(change, currency)}
        </span>
      ) : null}

      <dl className="grid grid-cols-[auto_1fr] gap-x-4 text-sm">
        <dt className="text-muted-foreground">Bill</dt>
        <dd className="tabular-nums text-foreground">{formatMoney(bill.total, currency)}</dd>

        {Number(bill.tipAmount) > 0 ? (
          <>
            <dt className="text-muted-foreground">Tip</dt>
            <dd className="tabular-nums text-foreground">
              {formatMoney(bill.tipAmount, currency)}
            </dd>
          </>
        ) : null}
      </dl>
    </section>
  )
}
