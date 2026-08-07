/**
 * Server state for the Z-report, the daily report and margins.
 *
 * All three are money- and drawer-adjacent, so all three take `LIVE_QUERY_OPTIONS`.
 * The 60-second catalog staleness is for product names; a report of an open
 * shift changes with every sale, and a stale expected-cash figure is one a
 * manager would count a drawer against.
 */

import { useQuery } from '@tanstack/react-query'
import { api, unwrap } from '@/api/client'
import type { components } from '@/api/schema'
import { LIVE_QUERY_OPTIONS } from '@/app/queryClient'

export type Report = components['schemas']['ReportResponse']
export type MarginReport = components['schemas']['MarginReportResponse']

export const reportKeys = {
  shift: (shiftId: string) => ['reports', 'shift', shiftId] as const,
  daily: (date: string | null) => ['reports', 'daily', date] as const,
  margins: (from: string | null, to: string | null) => ['reports', 'margins', from, to] as const,
}

/** The Z-report for one shift. */
export function useShiftReport(shiftId: string | null) {
  return useQuery({
    queryKey: reportKeys.shift(shiftId ?? ''),
    queryFn: () =>
      unwrap(api.GET('/api/v1/shifts/{id}/report', { params: { path: { id: shiftId ?? '' } } })),
    enabled: shiftId !== null,
    ...LIVE_QUERY_OPTIONS,
  })
}

/**
 * The report for one trading day.
 *
 * `date` omitted means the tenant's **current** trading day, which the server
 * resolves — not the browser's idea of today. At 01:00 in a shop with a 04:00
 * day start those are different days, and the one somebody wants is the one
 * they are still working.
 */
export function useDailyReport(date: string | null) {
  return useQuery({
    queryKey: reportKeys.daily(date),
    queryFn: () =>
      unwrap(
        api.GET('/api/v1/reports/daily', {
          params: { query: { date: date ?? undefined } },
        }),
      ),
    ...LIVE_QUERY_OPTIONS,
  })
}

/** Margin by product. Owner-only server-side; the UI gate is a courtesy. */
export function useMargins(from: string | null, to: string | null, enabled: boolean) {
  return useQuery({
    queryKey: reportKeys.margins(from, to),
    queryFn: () =>
      unwrap(
        api.GET('/api/v1/reports/margins', {
          params: { query: { from: from ?? undefined, to: to ?? undefined } },
        }),
      ),
    enabled,
    ...LIVE_QUERY_OPTIONS,
  })
}
