/**
 * The restaurant's server state: the room, the orders on it, the bills and the pass.
 *
 * **Order state is server-owned, and that is the deliberate departure from the
 * retail till.** The cart lives in `sessionStorage` so it survives a reload with
 * the line down; an order lives on the server so the second handheld can see the
 * table. They are different problems with different right answers, and this file
 * is the whole of the second one — there is no `OrderProvider` and no mirror of
 * an order anywhere on the client.
 *
 * Invariant 11 is not weakened by that: nothing new reaches storage, and the
 * manager's grant still lives only in `OverrideProvider`.
 *
 * Every write here takes its `Idempotency-Key` from the caller (`useOperationKey`),
 * never from inside a mutation — see `idempotency.ts`.
 */

import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, unwrap } from '@/api/client'
import type { components } from '@/api/schema'
import { CATALOG_STALE_MS, LIVE_QUERY_OPTIONS } from '@/app/queryClient'

export type ServiceArea = components['schemas']['ServiceAreaResponse']
export type DiningTable = components['schemas']['DiningTableResponse']
export type Order = components['schemas']['OrderResponse']
export type OrderLine = components['schemas']['OrderLineResponse']
export type OrderBill = components['schemas']['OrderBillResponse']
export type KitchenTicket = components['schemas']['KitchenTicketResponse']
export type Station = components['schemas']['StationResponse']
export type ModifierGroup = components['schemas']['ModifierGroupResponse']

export const restaurantKeys = {
  floor: () => ['floor'] as const,
  orders: (status: string) => ['orders', status] as const,
  order: (orderId: string) => ['orders', orderId] as const,
  bills: (orderId: string) => ['orders', orderId, 'bills'] as const,
  stations: () => ['stations'] as const,
  tickets: (stationId: string | null, includeBumped: boolean) =>
    ['kitchen', 'tickets', stationId, includeBumped] as const,
  modifierGroups: (productId: string) => ['menu', 'products', productId] as const,
}

// ─────────────────────────────────────────────────────────────── the room

/**
 * The areas and their tables.
 *
 * Catalog staleness, not live: a floor plan changes when a manager edits it,
 * which is not during service. What *is* live is which tables are occupied, and
 * that is a different question answered by `useOpenOrders` — a table carries no
 * occupied flag, deliberately (see `DiningTable`'s remarks in Pos.Core).
 */
export function useFloor() {
  return useQuery({
    queryKey: restaurantKeys.floor(),
    queryFn: () => unwrap(api.GET('/api/v1/floor')),
    staleTime: CATALOG_STALE_MS,
  })
}

/**
 * Every open order, oldest first — what makes a table read as occupied.
 *
 * `LIVE_QUERY_OPTIONS`, because this is the answer to "can I seat this table?"
 * and a stale yes is a waiter walking a party to a table with a bill on it.
 * Lines are not included by the endpoint; the detail read is one tap away.
 */
export function useOpenOrders() {
  return useQuery({
    queryKey: restaurantKeys.orders('Open'),
    queryFn: () => unwrap(api.GET('/api/v1/orders')),
    ...LIVE_QUERY_OPTIONS,
  })
}

/** One order and its lines. */
export function useOrder(orderId: string | null) {
  return useQuery({
    queryKey: restaurantKeys.order(orderId ?? ''),
    queryFn: () => unwrap(api.GET('/api/v1/orders/{id}', { params: { path: { id: orderId! } } })),
    enabled: orderId !== null,
    ...LIVE_QUERY_OPTIONS,
  })
}

/**
 * Seats a table, starts a tab, or takes a takeaway.
 *
 * A `409 table-already-occupied` is not a bug to swallow: two staff seated one
 * table in the same second and this one lost the filtered unique index. The
 * caller re-reads and shows the order that is already there.
 */
export function useSeatTable(idempotencyKey: string) {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: (input: {
      type: 'Table' | 'Tab' | 'Takeaway'
      diningTableId?: string | null
      tabName?: string | null
      coverCount?: number | null
    }) =>
      unwrap(
        api.POST('/api/v1/orders', {
          params: { header: { 'Idempotency-Key': idempotencyKey } },
          body: {
            type: input.type,
            diningTableId: input.diningTableId ?? null,
            tabName: input.tabName ?? null,
            coverCount: input.coverCount ?? null,
            note: null,
          },
        }),
      ),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['orders'] })
    },
  })
}

// ─────────────────────────────────────────────────────────── the order

/** What the till is asking the guest, for one product. */
export function useModifierGroups(productId: string | null) {
  return useQuery({
    queryKey: restaurantKeys.modifierGroups(productId ?? ''),
    queryFn: () =>
      unwrap(
        api.GET('/api/v1/menu/products/{productId}/modifier-groups', {
          params: { path: { productId: productId! } },
        }),
      ),
    enabled: productId !== null,
    staleTime: CATALOG_STALE_MS,
  })
}

/** One item to put on an order, with whatever the guest chose. */
export interface LineToAdd {
  productId: string
  quantity: number
  course: number
  seatNumber: number | null
  note: string | null
  modifiers: { productId: string; quantity: number }[]
}

/**
 * Puts items on an order.
 *
 * The whole round goes in one call rather than one call per item: a waiter keys
 * four covers standing at the table, and four requests is four chances for the
 * network to drop one silently.
 */
export function useAddLines(orderId: string, idempotencyKey: string) {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: (lines: readonly LineToAdd[]) =>
      unwrap(
        api.POST('/api/v1/orders/{id}/lines', {
          params: { path: { id: orderId }, header: { 'Idempotency-Key': idempotencyKey } },
          body: {
            lines: lines.map((line) => ({
              productId: line.productId,
              quantity: line.quantity,
              course: line.course,
              seatNumber: line.seatNumber,
              note: line.note,
              unitPriceOverride: null,
              discountAmount: null,
              modifiers: line.modifiers.map((modifier) => ({
                productId: modifier.productId,
                quantity: modifier.quantity,
                course: line.course,
                seatNumber: line.seatNumber,
                note: null,
                unitPriceOverride: null,
                discountAmount: null,
                modifiers: null,
              })),
            })),
          },
        }),
      ),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: restaurantKeys.order(orderId) })
    },
  })
}

/**
 * Takes a line off an order.
 *
 * **A reason is only required for a fired line**, and the server is what decides
 * that — this sends whatever the caller collected. A pending line coming off is
 * a guest changing their mind before anybody cooked; a fired one is a plate the
 * shop has lost, and it needs `CanVoidFiredLine` and an audit entry.
 *
 * **A manager's grant is presented when there is one**, exactly as the retail
 * till presents one for a discount. A waiter on a shared handheld cannot hand
 * the device over for every cancelled steak, so the alternative is a supervisor
 * session left open all evening — which would attribute every later void to
 * somebody who was not there. The grant is minted per void and spent by it.
 *
 * A `403` with `type: .../override-required` is the caller's cue to open
 * `ManagerAuthorizationDialog`, not to show a dead end.
 */
export function useVoidLine(orderId: string) {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: (input: { lineId: string; reason: string | null; grant: string | null }) =>
      unwrap(
        api.POST('/api/v1/orders/{id}/lines/{lineId}/void', {
          params: {
            path: { id: orderId, lineId: input.lineId },
            header: { 'X-Override-Authorization': input.grant ?? undefined },
          },
          body: { reason: input.reason },
        }),
      ),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: restaurantKeys.order(orderId) })
      await queryClient.invalidateQueries({ queryKey: ['kitchen'] })
    },
  })
}

/**
 * Sends a round to the kitchen.
 *
 * An empty ticket list is a **success**, not a failure: it means the round is
 * already away, which is what a second tap looks like. The caller says so rather
 * than showing an error for something that did exactly what was wanted.
 */
export function useFire(orderId: string, idempotencyKey: string) {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: (course: number | null) =>
      unwrap(
        api.POST('/api/v1/orders/{id}/fire', {
          params: { path: { id: orderId }, header: { 'Idempotency-Key': idempotencyKey } },
          body: { course },
        }),
      ),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: restaurantKeys.order(orderId) })
      await queryClient.invalidateQueries({ queryKey: ['kitchen'] })
    },
  })
}

// ─────────────────────────────────────────────────────────────── bills

/**
 * The bills on an order, priced.
 *
 * **These amounts are a quote until the bill is paid.** Nothing is stored — the
 * endpoint prices the allocated lines through the same engine the payment will
 * use, so re-reading after a line is voided returns different numbers, correctly.
 */
export function useBills(orderId: string | null) {
  return useQuery({
    queryKey: restaurantKeys.bills(orderId ?? ''),
    queryFn: () =>
      unwrap(api.GET('/api/v1/orders/{id}/bills', { params: { path: { id: orderId! } } })),
    enabled: orderId !== null,
    ...LIVE_QUERY_OPTIONS,
  })
}

/** How much of one order line a bill takes. A quantity, because a bottle is shared. */
export interface BillAllocation {
  orderLineId: string
  quantity: number
}

/**
 * Opens a bill over some or all of the unbilled lines.
 *
 * Omitting `allocations` takes everything still unbilled, which is the ordinary
 * case — one table, one bill. Splitting is the exception.
 */
export function useCreateBill(orderId: string, idempotencyKey: string) {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: (allocations: readonly BillAllocation[] | null) =>
      unwrap(
        api.POST('/api/v1/orders/{id}/bills', {
          params: { path: { id: orderId }, header: { 'Idempotency-Key': idempotencyKey } },
          body: {
            allocations:
              allocations === null
                ? null
                : allocations.map((allocation) => ({
                    orderLineId: allocation.orderLineId,
                    quantity: allocation.quantity,
                  })),
          },
        }),
      ),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: restaurantKeys.bills(orderId) })
      await queryClient.invalidateQueries({ queryKey: restaurantKeys.order(orderId) })
    },
  })
}

/**
 * Settles a bill, which commits an ordinary sale.
 *
 * **The register and the shift travel in the body**, and they are the *current*
 * ones — a table opened on the terrace handheld and settled at the bar belongs
 * to the bar's drawer, because that is where the cash physically is.
 *
 * The key belongs to the bill and was minted when it was opened. An even split
 * is N tenders here, not N bills.
 */
export function usePayBill(orderId: string, idempotencyKey: string) {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: (input: {
      billId: string
      registerId: string
      shiftId: string
      tenders: readonly { amount: number }[]
      tip: number | null
    }) =>
      unwrap(
        api.POST('/api/v1/orders/{id}/bills/{billId}/pay', {
          params: {
            path: { id: orderId, billId: input.billId },
            header: { 'Idempotency-Key': idempotencyKey },
          },
          body: {
            registerId: input.registerId,
            shiftId: input.shiftId,
            tenders: input.tenders.map((tender) => ({ amount: tender.amount })),
            tip: input.tip,
          },
        }),
      ),
    onSuccess: async () => {
      // The order may have closed itself, and the drawer has moved.
      await queryClient.invalidateQueries({ queryKey: ['orders'] })
      await queryClient.invalidateQueries({ queryKey: ['shifts'] })
    },
  })
}

// ───────────────────────────────────────────────────────────── the pass

/** The kitchen's stations. */
export function useStations() {
  return useQuery({
    queryKey: restaurantKeys.stations(),
    queryFn: () => unwrap(api.GET('/api/v1/stations')),
    staleTime: CATALOG_STALE_MS,
  })
}

/**
 * One station's queue, refreshed on an interval.
 *
 * **The display polls, and the interval is the server's number** — see
 * `KitchenEndpoints.PollIntervalSeconds`. There is no SSE and no websocket in
 * this project; adding a transport is its own phase with its own reconnection,
 * authentication and proxy problems, and a screen refreshing every few seconds
 * is indistinguishable from a live one at the speed food is cooked.
 *
 * `refetchIntervalInBackground` stays off: a kitchen screen that is not visible
 * is a tab nobody is standing at.
 */
export function useKitchenTickets(
  stationId: string | null,
  includeBumped: boolean,
  intervalMs: number,
) {
  return useQuery({
    queryKey: restaurantKeys.tickets(stationId, includeBumped),
    queryFn: () =>
      unwrap(
        api.GET('/api/v1/kitchen/tickets', {
          params: { query: { stationId: stationId ?? undefined, includeBumped } },
        }),
      ),
    refetchInterval: intervalMs,
    ...LIVE_QUERY_OPTIONS,
  })
}

/**
 * Clears a ticket, or puts a cleared one back.
 *
 * **No idempotency key**, unlike every write above: bumping moves no money and
 * no stock, and it is idempotent by state — bumping a bumped ticket is already
 * a no-op and answers 200 rather than a conflict, because two chefs reaching
 * for one screen is ordinary. Requiring a key on something done forty times an
 * hour would be friction that buys nothing.
 */
export function useBumpTicket() {
  const queryClient = useQueryClient()

  return useMutation({
    mutationFn: (input: { ticketId: string; action: 'bump' | 'recall' }) =>
      input.action === 'bump'
        ? unwrap(
            api.POST('/api/v1/kitchen/tickets/{id}/bump', {
              params: { path: { id: input.ticketId } },
            }),
          )
        : unwrap(
            api.POST('/api/v1/kitchen/tickets/{id}/recall', {
              params: { path: { id: input.ticketId } },
            }),
          ),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ['kitchen'] })
    },
  })
}
