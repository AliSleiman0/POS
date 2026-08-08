import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, unwrap } from '@/api/client'
import type { components } from '@/api/schema'

export type EmployeeSummary = components['schemas']['EmployeeSummary']
export type EmployeeRole = 'Cashier' | 'Manager' | 'Owner'

/**
 * The three roles, in the order an owner thinks about them.
 *
 * Mirrors `RoleNames.All` on the server, which is the authority — a role the
 * server does not know is a 400, not a broken screen. The descriptions are the
 * policy table in `docs/ARCHITECTURE.md` said in a sentence, because "Manager"
 * on its own does not tell an owner whether that person can issue refunds.
 */
export const ROLES: ReadonlyArray<{ value: EmployeeRole; label: string; hint: string }> = [
  { value: 'Cashier', label: 'Cashier', hint: 'Sell only. No discounts, refunds or reports.' },
  {
    value: 'Manager',
    label: 'Manager',
    hint: 'Discounts, refunds, voids, the catalog and the day’s reports.',
  },
  { value: 'Owner', label: 'Owner', hint: 'Everything, including staff, margins and settings.' },
]

export const employeeKeys = {
  all: ['employees'] as const,
  list: (activeOnly: boolean) => ['employees', { activeOnly }] as const,
}

/**
 * Every member of staff.
 *
 * A plain `useQuery`, not `useInfiniteQuery`: the server answers with a bare
 * array because `ApplicationUser` cannot use the cursor helper, and a shop has
 * tens of staff. See docs/API.md.
 */
export function useEmployees(activeOnly: boolean) {
  return useQuery({
    queryKey: employeeKeys.list(activeOnly),
    queryFn: () =>
      unwrap(
        api.GET('/api/v1/employees', {
          // Omitted rather than sent as false, so the URL says what it means.
          params: { query: activeOnly ? { activeOnly: true } : {} },
        }),
      ),
  })
}

export function useCreateEmployee() {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: (body: components['schemas']['CreateEmployeeRequest']) =>
      unwrap(api.POST('/api/v1/employees', { body })),
    onSuccess: () => invalidate(queryClient),
  })
}

export function useUpdateEmployee() {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: (input: { id: string; body: components['schemas']['UpdateEmployeeRequest'] }) =>
      unwrap(
        api.PUT('/api/v1/employees/{id}', {
          params: { path: { id: input.id } },
          body: input.body,
        }),
      ),
    onSuccess: () => invalidate(queryClient),
  })
}

export function useDeactivateEmployee() {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: (id: string) =>
      unwrap(api.POST('/api/v1/employees/{id}/deactivate', { params: { path: { id } } })),
    onSuccess: () => invalidate(queryClient),
  })
}

export function useSetPin() {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: (input: { id: string; pin: string }) =>
      unwrap(
        api.POST('/api/v1/employees/{id}/set-pin', {
          params: { path: { id: input.id } },
          body: { pin: input.pin },
        }),
      ),
    // The list shows a `hasPin` column, so it is stale after this.
    onSuccess: () => invalidate(queryClient),
  })
}

/**
 * Both list variants, because a change moves a row between them.
 *
 * Deactivating somebody removes them from the active-only list and changes their
 * status in the full one; invalidating only the list currently on screen would
 * leave the other wrong until it happened to refetch.
 */
function invalidate(queryClient: ReturnType<typeof useQueryClient>) {
  return queryClient.invalidateQueries({ queryKey: employeeKeys.all })
}
