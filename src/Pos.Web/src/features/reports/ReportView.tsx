import { formatMoney, formatRate, parseServerDecimal } from '@/lib/money'
import { cn } from '@/lib/utils'
import type { Report } from './queries'

/**
 * A report, laid out the way somebody reconciles one.
 *
 * The same component for a shift's Z-report and for a trading day, because the
 * server returns the same shape for both — a shift's report and the day that
 * contains it have to add up to the same money, and two layouts would invite two
 * sets of arithmetic.
 *
 * **Every figure here is the server's.** The client sums nothing: the tax rows
 * already add up to the tax total because the server placed the rounding residue
 * deliberately, and a total computed in the browser would be a second opinion
 * about money (CLAUDE.md invariant 3).
 */
export function ReportView({ report }: { report: Report }) {
  const currency = report.currencyCode

  return (
    <div className="flex flex-col gap-6">
      <Section title="Sales">
        <Figures>
          <Figure label="Transactions" value={String(report.sales.transactionCount)} />
          <Figure label="Gross" value={formatMoney(report.sales.gross, currency)} />
          <Figure label="Discounts" value={formatMoney(report.sales.discounts, currency)} />
          <Figure label="Net" value={formatMoney(report.sales.net, currency)} />
          <Figure label="Tax" value={formatMoney(report.sales.tax, currency)} />
          {parseServerDecimal(report.sales.rounding) !== 0 ? (
            <Figure label="Cash rounding" value={formatMoney(report.sales.rounding, currency)} />
          ) : null}
          <Figure
            label="Total"
            value={formatMoney(report.sales.total, currency)}
            emphasis
            testId="report-total"
          />
          <Figure
            label="Average basket"
            value={formatMoney(report.sales.averageBasket, currency)}
          />
          {/* `refundCount` is an int32 and the generated client still types it
              `number | string` — the same union `ServerDecimal` documents, which
              a `> 0` on the raw value does not compile against and a `Number()`
              would accept nonsense from. */}
          {parseServerDecimal(report.sales.refundCount) > 0 ? (
            <Figure
              label={`Refunds (${String(report.sales.refundCount)})`}
              value={formatMoney(report.sales.refundTotal, currency)}
            />
          ) : null}
        </Figures>
      </Section>

      <Section title="Tax by rate" testId="report-tax-by-rate">
        {report.taxByRate.length === 0 ? (
          <Nothing>No sales in this period.</Nothing>
        ) : (
          <Table head={['Rate', right('Net'), right('Tax')]}>
            {report.taxByRate.map((line) => (
              <tr key={String(line.rate)} className="border-t border-border">
                <Cell>{formatRate(line.rate)}</Cell>
                <Cell align="right">{formatMoney(line.net, currency)}</Cell>
                <Cell align="right">{formatMoney(line.tax, currency)}</Cell>
              </tr>
            ))}
          </Table>
        )}
      </Section>

      <Section title="Payments">
        {report.tenders.length === 0 ? (
          <Nothing>Nothing was taken.</Nothing>
        ) : (
          <Table head={['Method', right('Taken'), right('Change'), right('Net')]}>
            {report.tenders.map((tender) => (
              <tr key={tender.method} className="border-t border-border">
                <Cell>{tender.method}</Cell>
                <Cell align="right">{formatMoney(tender.amount, currency)}</Cell>
                <Cell align="right">{formatMoney(tender.changeGiven, currency)}</Cell>
                <Cell align="right">{formatMoney(tender.net, currency)}</Cell>
              </tr>
            ))}
          </Table>
        )}
      </Section>

      <Section title="Cash reconciliation" testId="report-cash">
        {/* The distinction the server draws and the UI must not lose. A closed
            shift's variance was stored when it was counted; an open one has
            never been counted, so what is shown is an expectation and says so.
            A reader who took a provisional figure for a reconciled one would
            "find" a variance that nobody has measured. */}
        {report.cash.isProvisional ? (
          <p
            role="status"
            data-testid="report-provisional"
            className="mb-3 rounded-md border border-border bg-muted px-3 py-2 text-sm text-muted-foreground"
          >
            <span className="font-medium text-foreground">A drawer is still open.</span> The
            expected figure is what should be in it right now. Nothing has been counted, so there is
            no variance yet.
          </p>
        ) : null}

        <Figures>
          <Figure label="Opening float" value={formatMoney(report.cash.openingFloat, currency)} />
          <Figure label="Cash sales" value={formatMoney(report.cash.cashSales, currency)} />
          {parseServerDecimal(report.cash.cashRefunds) !== 0 ? (
            <Figure label="Cash refunds" value={formatMoney(report.cash.cashRefunds, currency)} />
          ) : null}
          <Figure
            label="Drops and payouts"
            value={formatMoney(report.cash.cashMovements, currency)}
          />
          <Figure
            label="Expected"
            value={formatMoney(report.cash.expected, currency)}
            emphasis
            testId="report-expected-cash"
          />
          <Figure
            label="Counted"
            value={
              report.cash.counted === null
                ? 'Not counted'
                : formatMoney(report.cash.counted, currency)
            }
          />
          {report.cash.variance !== null ? (
            <Figure
              label="Variance"
              value={formatMoney(report.cash.variance, currency)}
              testId="report-variance"
              // The one number an owner looks for, and the sign carries the
              // meaning: negative is short.
              tone={parseServerDecimal(report.cash.variance) < 0 ? 'bad' : 'good'}
              emphasis
            />
          ) : null}
        </Figures>

        {report.cash.movements.length > 0 ? (
          <div className="mt-3">
            <Table head={['When', 'Type', right('Amount'), 'Reason', 'By']}>
              {report.cash.movements.map((movement) => (
                <tr key={movement.id} className="border-t border-border">
                  <Cell>{formatStamp(movement.occurredAt, report.scope.timeZoneId)}</Cell>
                  <Cell>{movement.type}</Cell>
                  <Cell align="right">{formatMoney(movement.amount, currency)}</Cell>
                  <Cell>{movement.reason}</Cell>
                  <Cell>{movement.performedBy}</Cell>
                </tr>
              ))}
            </Table>
          </div>
        ) : null}
      </Section>

      {report.shifts.length > 0 ? (
        <Section title="Shifts">
          <Table
            head={[
              'Register',
              'Opened',
              'Closed',
              right('Expected'),
              right('Counted'),
              right('Variance'),
            ]}
          >
            {report.shifts.map((shift) => (
              <tr key={shift.id} className="border-t border-border">
                <Cell>{shift.registerName}</Cell>
                <Cell>
                  {formatStamp(shift.openedAt, report.scope.timeZoneId)} · {shift.openedBy}
                </Cell>
                <Cell>
                  {shift.closedAt === null
                    ? 'Open'
                    : `${formatStamp(shift.closedAt, report.scope.timeZoneId)} · ${shift.closedBy ?? ''}`}
                </Cell>
                <Cell align="right">
                  {shift.expectedCash === null ? '—' : formatMoney(shift.expectedCash, currency)}
                </Cell>
                <Cell align="right">
                  {shift.countedCash === null ? '—' : formatMoney(shift.countedCash, currency)}
                </Cell>
                <Cell align="right">
                  {shift.variance === null ? '—' : formatMoney(shift.variance, currency)}
                </Cell>
              </tr>
            ))}
          </Table>
        </Section>
      ) : null}

      {/* Listed individually with the actor and the reason, which is the whole
          point of showing them: a count of voids is a number nobody can act on,
          while "four, all by the same person, all reason 'mistake'" is what an
          owner opened the report to find. */}
      <Reversals
        title="Voids"
        testId="report-voids"
        empty="No sales were voided."
        rows={report.voids}
        currency={currency}
        timeZoneId={report.scope.timeZoneId}
      />

      <Reversals
        title="Refunds"
        testId="report-refunds"
        empty="Nothing was refunded."
        rows={report.refunds}
        currency={currency}
        timeZoneId={report.scope.timeZoneId}
        showsOriginal
      />
    </div>
  )
}

function Reversals({
  title,
  testId,
  empty,
  rows,
  currency,
  timeZoneId,
  showsOriginal = false,
}: {
  title: string
  testId: string
  empty: string
  rows: Report['voids']
  currency: string
  timeZoneId: string
  showsOriginal?: boolean
}) {
  return (
    <Section title={title} testId={testId}>
      {rows.length === 0 ? (
        <Nothing>{empty}</Nothing>
      ) : (
        <Table
          head={[
            'Sale',
            'When',
            right('Amount'),
            'By',
            'Reason',
            ...(showsOriginal ? ['Against'] : []),
          ]}
        >
          {rows.map((row) => (
            <tr key={row.saleId} className="border-t border-border">
              <Cell>#{String(row.saleNumber)}</Cell>
              <Cell>{formatStamp(row.at, timeZoneId)}</Cell>
              <Cell align="right">{formatMoney(row.total, currency)}</Cell>
              <Cell>{row.actor}</Cell>
              <Cell>{row.reason ?? '—'}</Cell>
              {showsOriginal ? (
                <Cell>
                  {row.originalSaleNumber === null ? '—' : `#${String(row.originalSaleNumber)}`}
                </Cell>
              ) : null}
            </tr>
          ))}
        </Table>
      )}
    </Section>
  )
}

function Section({
  title,
  testId,
  children,
}: {
  title: string
  testId?: string
  children: React.ReactNode
}) {
  return (
    <section data-testid={testId} className="rounded-xl border border-border p-4">
      <h2 className="mb-3 text-sm font-semibold text-foreground">{title}</h2>
      {children}
    </section>
  )
}

/**
 * A column of labelled figures.
 *
 * **One column, not two.** These are a chain — gross, less discounts, net, plus
 * tax, total — and a two-column grid lays them out row-major, so the eye reads
 * "Transactions, Gross, Discounts, Net" and the order the arithmetic goes in is
 * lost. A reconciliation people check line by line has to be readable line by
 * line.
 */
function Figures({ children }: { children: React.ReactNode }) {
  return <dl className="max-w-md">{children}</dl>
}

function Figure({
  label,
  value,
  emphasis = false,
  tone,
  testId,
}: {
  label: string
  value: string
  emphasis?: boolean
  tone?: 'good' | 'bad'
  testId?: string
}) {
  return (
    <div className="flex items-baseline justify-between gap-4 border-b border-border/50 py-1 last:border-0">
      <dt className="text-sm text-muted-foreground">{label}</dt>
      <dd
        data-testid={testId}
        className={cn(
          'text-sm tabular-nums text-foreground',
          emphasis && 'font-semibold',
          tone === 'bad' && 'text-destructive',
        )}
      >
        {value}
      </dd>
    </div>
  )
}

/** A column heading, and which way its cells are aligned. */
type Column = string | { label: string; align: 'right' }

/**
 * A table whose headings line up with its cells.
 *
 * **The alignment is stated per column rather than inferred**, because inferring
 * it is what went wrong: "everything except the first and last is right-aligned"
 * is true of no table here, and it put a left-aligned `Tax` heading against a
 * right-aligned `Net` one — which rendered as `NetTax`, one word, over two
 * columns of numbers. Visible instantly in a screenshot and invisible to jsdom.
 */
function Table({ head, children }: { head: Column[]; children: React.ReactNode }) {
  return (
    <div className="overflow-x-auto">
      <table className="w-full text-sm">
        <thead>
          <tr>
            {head.map((column) => {
              const label = typeof column === 'string' ? column : column.label
              const right = typeof column !== 'string' && column.align === 'right'

              return (
                <th
                  key={label}
                  className={cn(
                    'pb-1 pr-3 font-medium text-muted-foreground last:pr-0',
                    right ? 'text-right' : 'text-left',
                  )}
                >
                  {label}
                </th>
              )
            })}
          </tr>
        </thead>
        <tbody>{children}</tbody>
      </table>
    </div>
  )
}

/** Shorthand for a numeric column. */
function right(label: string): Column {
  return { label, align: 'right' }
}

function Cell({
  children,
  align = 'left',
}: {
  children: React.ReactNode
  align?: 'left' | 'right'
}) {
  return (
    <td
      className={cn(
        'py-1 pr-3 text-foreground last:pr-0',
        align === 'right' && 'text-right tabular-nums',
      )}
    >
      {children}
    </td>
  )
}

function Nothing({ children }: { children: React.ReactNode }) {
  return <p className="text-sm text-muted-foreground">{children}</p>
}

/**
 * A timestamp in the tenant's zone.
 *
 * `timeZoneId` comes from the report, so a manager looking at a shop's figures
 * from another country reads the shop's clock — the same times printed on its
 * receipts. Deriving it from the browser would make the two disagree, and the
 * report is the thing people reconcile receipts against.
 */
function formatStamp(value: string, timeZoneId: string): string {
  return new Intl.DateTimeFormat(undefined, {
    timeZone: timeZoneId,
    day: '2-digit',
    month: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
  }).format(new Date(value))
}
