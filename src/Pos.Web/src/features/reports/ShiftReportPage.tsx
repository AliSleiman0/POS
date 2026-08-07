import { Link, useParams } from 'react-router'
import { ErrorState, LoadingState } from '@/components/states'
import { formatMoney } from '@/lib/money'
import { ReportView } from './ReportView'
import { useShiftReport } from './queries'

/**
 * The Z-report for one shift — the drawer, and whether it balanced.
 *
 * Reached from the daily report's shift list and from the close flow, which is
 * where it is actually wanted: somebody has just counted a drawer and needs to
 * see what it should have held.
 */
export function ShiftReportPage() {
  const { shiftId = null } = useParams<{ shiftId: string }>()
  const report = useShiftReport(shiftId)

  return (
    <div className="flex flex-col gap-4 p-4">
      <header className="flex flex-wrap items-end justify-between gap-4">
        <div>
          <h1 className="text-lg font-semibold text-foreground">Z-report</h1>
          {report.isSuccess ? (
            <p className="text-sm text-muted-foreground">
              {report.data.shifts.length > 0
                ? `${report.data.shifts[0]!.registerName} · opened by ${report.data.shifts[0]!.openedBy}`
                : 'This shift has no record.'}
            </p>
          ) : null}
        </div>

        <div className="flex items-baseline gap-4">
          {report.isSuccess ? (
            <p className="text-sm text-muted-foreground">
              Total{' '}
              <span className="font-semibold text-foreground">
                {formatMoney(report.data.sales.total, report.data.currencyCode)}
              </span>
            </p>
          ) : null}
          <Link to="/reports/daily" className="text-sm text-muted-foreground hover:text-foreground">
            The whole day
          </Link>
        </div>
      </header>

      {report.isPending ? (
        <LoadingState label="Adding the shift up…" />
      ) : report.isError ? (
        <ErrorState
          error={report.error}
          title="That shift's report could not be produced."
          onRetry={() => {
            void report.refetch()
          }}
        />
      ) : (
        <ReportView report={report.data} />
      )}
    </div>
  )
}
