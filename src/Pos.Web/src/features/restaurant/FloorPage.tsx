import { useState } from 'react'
import { useNavigate } from 'react-router'
import { Button } from '@/components/ui/button'
import { EmptyState, ErrorState, LoadingState } from '@/components/states'
import { useToast } from '@/components/toastContext'
import { ErrorType, isErrorType } from '@/api/problem'
import { useOffline } from '@/features/offline/offlineContext'
import { parseServerDecimal } from '@/lib/money'
import { useOperationKey } from './idempotency'
import { useFloor, useOpenOrders, useSeatTable, type DiningTable, type Order } from './queries'

/**
 * The room.
 *
 * **A table is free when no open order points at it**, which is one question
 * with one answer. `DiningTable` deliberately carries no occupied flag: a second
 * copy of that fact would disagree with the first the moment a request failed
 * between updating them, leaving a table that reads as busy with nothing on it —
 * or worse, free with a bill still open.
 *
 * A list, not a floor plan. There are no coordinates and no canvas editor; that
 * is a stated non-goal of the phase rather than an omission, because a
 * drag-and-drop designer is a week of work that changes nothing about whether
 * the till is correct.
 */
export function FloorPage() {
  const floor = useFloor()
  const orders = useOpenOrders()

  if (floor.isPending || orders.isPending) {
    return <LoadingState label="Reading the room…" />
  }

  if (floor.isError) {
    return (
      <ErrorState title="Could not read the floor." error={floor.error} onRetry={floor.refetch} />
    )
  }

  if (orders.isError) {
    return (
      <ErrorState
        title="Could not read the open tables."
        error={orders.error}
        onRetry={orders.refetch}
      />
    )
  }

  const areas = floor.data ?? []
  const open = orders.data ?? []

  if (areas.length === 0) {
    return (
      <EmptyState
        title="No areas yet."
        description="A manager adds the room's areas and tables under Settings before service can start."
      />
    )
  }

  return (
    <div className="flex flex-col gap-6 p-4">
      <OfflineNotice />

      {areas.map((area) => (
        <section key={area.id} aria-label={area.name} className="flex flex-col gap-3">
          <h2 className="text-sm font-semibold text-foreground">{area.name}</h2>

          {area.tables.length === 0 ? (
            <p className="text-sm text-muted-foreground">No tables in this area.</p>
          ) : (
            <div className="grid grid-cols-[repeat(auto-fill,minmax(9rem,1fr))] gap-3">
              {area.tables.map((table) => (
                <TableCard
                  key={table.id}
                  table={table}
                  order={open.find((candidate) => candidate.diningTableId === table.id) ?? null}
                />
              ))}
            </div>
          )}
        </section>
      ))}

      <Tabs orders={open.filter((order) => order.diningTableId === null)} />
    </div>
  )
}

/**
 * One table.
 *
 * Occupied shows the order number staff shout and how long the party has been
 * sitting — the two things a floor manager scans for. Free is a button.
 */
function TableCard({ table, order }: { table: DiningTable; order: Order | null }) {
  const navigate = useNavigate()
  const toast = useToast()
  const offline = useOffline()
  const key = useOperationKey()
  const seat = useSeatTable(key.key)

  const occupied = order !== null

  // ServerDecimal is number | string deliberately (CLAUDE.md): checking against
  // the type rather than against what the API happens to send today.
  const seats = Number(parseServerDecimal(table.seats))

  const open = async () => {
    if (occupied) {
      await navigate(`/restaurant/orders/${order.id}`)
      return
    }

    try {
      const opened = await seat.mutateAsync({
        type: 'Table',
        diningTableId: table.id,
        coverCount: seats > 0 ? seats : null,
      })

      await navigate(`/restaurant/orders/${opened.id}`)
    } catch (caught) {
      // Somebody else got there first — the filtered unique index refused this
      // one. Not an error to apologise for: the honest answer is "it is taken",
      // and re-reading shows whose it is.
      if (isErrorType(caught, ErrorType.tableAlreadyOccupied)) {
        toast.show('That table was just seated by somebody else.', { tone: 'info' })
        return
      }

      toast.showError(caught, 'Could not seat that table.')
    }
  }

  return (
    <Button
      type="button"
      variant={occupied ? 'secondary' : 'outline'}
      // Taller than a default button: this is tapped with a thumb while walking.
      className="h-24 flex-col items-start justify-between gap-1 p-3 text-left"
      disabled={seat.isPending || offline.connectivity === 'offline'}
      onClick={() => void open()}
      data-testid={`table-${table.name}`}
    >
      <span className="text-base font-semibold">{table.name}</span>

      {occupied ? (
        <span className="text-xs font-normal text-muted-foreground">
          #{order.orderNumber}
          {order.coverCount === null ? '' : ` · ${order.coverCount} covers`}
        </span>
      ) : (
        <span className="text-xs font-normal text-muted-foreground">
          {seats > 0 ? `Seats ${String(seats)}` : 'Free'}
        </span>
      )}
    </Button>
  )
}

/** Tabs and takeaways, which have no table to sit on. */
function Tabs({ orders }: { orders: readonly Order[] }) {
  const navigate = useNavigate()

  if (orders.length === 0) {
    return null
  }

  return (
    <section aria-label="Tabs and takeaway" className="flex flex-col gap-3">
      <h2 className="text-sm font-semibold text-foreground">Tabs and takeaway</h2>

      <div className="grid grid-cols-[repeat(auto-fill,minmax(9rem,1fr))] gap-3">
        {orders.map((order) => (
          <Button
            key={order.id}
            type="button"
            variant="secondary"
            className="h-24 flex-col items-start justify-between gap-1 p-3 text-left"
            onClick={() => void navigate(`/restaurant/orders/${order.id}`)}
          >
            <span className="text-base font-semibold">{order.tabName ?? 'Takeaway'}</span>
            <span className="text-xs font-normal text-muted-foreground">#{order.orderNumber}</span>
          </Button>
        ))}
      </div>
    </section>
  )
}

/**
 * What a restaurant till cannot do with the line down.
 *
 * **Said here as well as on the diagnostics panel**, because this is the screen
 * somebody is standing at when it happens. An order lives on the server so a
 * second handheld can see the table; the retail cart lives in `sessionStorage`
 * so it can be rung with the line down. Phase 9's outbox queues *sales* and
 * knows nothing about orders.
 */
function OfflineNotice() {
  const offline = useOffline()
  const [dismissed, setDismissed] = useState(false)

  if (offline.connectivity !== 'offline' || dismissed) {
    return null
  }

  return (
    <div
      role="status"
      data-testid="floor-offline"
      className="flex items-start justify-between gap-3 rounded-lg border border-destructive/30 bg-destructive/5 p-3"
    >
      <p className="text-sm text-foreground">
        <strong className="font-semibold">No connection.</strong> Tables cannot be seated and orders
        cannot be taken until the server is reachable — an order lives on the server so every device
        sees the same table. Cash sales at the counter still work.
      </p>

      <Button type="button" variant="ghost" size="sm" onClick={() => setDismissed(true)}>
        Hide
      </Button>
    </div>
  )
}
