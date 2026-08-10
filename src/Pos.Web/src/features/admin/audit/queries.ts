import { useInfiniteQuery } from '@tanstack/react-query'
import { api, unwrap } from '@/api/client'
import type { components } from '@/api/schema'

export type AuditEntry = components['schemas']['AuditEntryResponse']
export type AuditAction = components['schemas']['AuditAction']

/**
 * The recorded actions, in the order an owner scans them.
 *
 * Grouped rather than alphabetical: money first, then access, then configuration.
 * A flat A-to-Z list puts `DeviceEnrolled` above `DiscountApplied`, which is not how
 * anybody thinks about what they are looking for.
 */
export const ACTIONS: ReadonlyArray<{ value: AuditAction; label: string }> = [
  { value: 'PriceOverridden', label: 'Price overridden' },
  { value: 'DiscountApplied', label: 'Discount applied' },
  { value: 'SaleVoided', label: 'Sale voided' },
  { value: 'RefundIssued', label: 'Refund issued' },
  { value: 'ReceiptIssued', label: 'Receipt issued' },
  { value: 'StockAdjusted', label: 'Stock adjusted' },
  { value: 'ShiftClosed', label: 'Drawer closed' },
  { value: 'AuthorizationRefused', label: 'Refused attempt' },
  { value: 'EmployeeCreated', label: 'Person added' },
  { value: 'EmployeeDeactivated', label: 'Person deactivated' },
  { value: 'RoleChanged', label: 'Role changed' },
  { value: 'PinReset', label: 'PIN set' },
  { value: 'DeviceEnrolled', label: 'Till enrolled' },
  { value: 'DeviceRevoked', label: 'Till revoked' },
  { value: 'SettingsChanged', label: 'Setting changed' },
]

/**
 * Filters, as a flat all-strings object.
 *
 * Empty string means "no filter", so the whole object is a stable query key and a
 * change to any field refetches — the same shape `SaleFilters` uses.
 */
export interface AuditFilters {
  action: string
  actorId: string
  from: string
  to: string
}

export const EMPTY_FILTERS: AuditFilters = { action: '', actorId: '', from: '', to: '' }

export const auditKeys = {
  list: (filters: AuditFilters) => ['audit', filters] as const,
}

export function useAuditEntries(filters: AuditFilters) {
  return useInfiniteQuery({
    queryKey: auditKeys.list(filters),
    initialPageParam: undefined as string | undefined,
    queryFn: ({ pageParam }) =>
      unwrap(
        api.GET('/api/v1/audit', {
          params: {
            query: {
              cursor: pageParam,
              action: blank(filters.action),
              actorId: blank(filters.actorId),

              // Sent as the shop's own trading days and resolved server-side through its
              // zone and day-start offset. Never a browser-derived date: a 02:00 void
              // belongs to the previous trading day, and only the server knows where that
              // boundary falls.
              from: blank(filters.from),
              to: blank(filters.to),
            },
          },
        }),
      ),
    getNextPageParam: (last) => last.nextCursor ?? undefined,

    // Always refetched. This is the screen somebody opens because they suspect
    // something, and a cached answer from ten minutes ago is the wrong one.
    staleTime: 0,
    refetchOnMount: 'always',
  })
}

function blank(value: string): string | undefined {
  return value === '' ? undefined : value
}
