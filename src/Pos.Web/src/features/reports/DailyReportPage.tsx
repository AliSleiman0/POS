import { useState } from 'react'
import { useAuth } from '@/auth/authContext'
import { ErrorState, LoadingState } from '@/components/states'
import { Field } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { formatMoney } from '@/lib/money'
import { MarginPanel } from './MarginPanel'
import { ReportView } from './ReportView'
import { useDailyReport } from './queries'

/**
 * What the shop did on one trading day.
 *
 * **The date is a trading day, not a calendar one.** Left empty it means the
 * tenant's current trading day, which the *server* resolves through its zone and
 * day-start offset — at 01:00 in a shop that starts its day at 04:00 that is
 * yesterday, and yesterday is the one somebody standing there wants. Defaulting
 * the field to the browser's `new Date()` would quietly ask for the wrong day
 * and answer plausibly.
 */
export function DailyReportPage() {
  const { can } = useAuth()

  // Empty means "the current trading day, whatever the server says that is".
  const [date, setDate] = useState('')

  const report = useDailyReport(date === '' ? null : date)

  return (
    <div className="flex flex-col gap-4 p-4">
      <header className="flex flex-wrap items-end justify-between gap-4">
        <div>
          <h1 className="text-lg font-semibold text-foreground">Daily report</h1>
          {report.isSuccess ? (
            <p className="text-sm text-muted-foreground">
              {report.data.scope.date ?? 'Today'} · trading day in {report.data.scope.timeZoneId}
            </p>
          ) : null}
        </div>

        <div className="flex items-end gap-3">
          <Field label="Trading day" hint="Empty means today">
            {(props) => (
              <Input
                {...props}
                type="date"
                value={date}
                onChange={(event) => {
                  setDate(event.target.value)
                }}
              />
            )}
          </Field>

          {report.isSuccess ? (
            <p className="pb-2 text-sm text-muted-foreground">
              Total{' '}
              <span className="font-semibold text-foreground">
                {formatMoney(report.data.sales.total, report.data.currencyCode)}
              </span>
            </p>
          ) : null}
        </div>
      </header>

      {report.isPending ? (
        <LoadingState label="Adding the day up…" />
      ) : report.isError ? (
        <ErrorState
          error={report.error}
          title="That day's report could not be produced."
          onRetry={() => {
            void report.refetch()
          }}
        />
      ) : (
        <>
          <ReportView report={report.data} />

          {/* Owner-only, and the server omits the route to anyone else regardless
              (invariant 7) — this gate only avoids showing a panel that would
              answer 403. */}
          {can('CanViewMargins') ? <MarginPanel date={report.data.scope.date} /> : null}
        </>
      )}
    </div>
  )
}
