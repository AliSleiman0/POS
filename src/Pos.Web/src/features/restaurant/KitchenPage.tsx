import { useEffect, useState } from 'react'
import { Button } from '@/components/ui/button'
import { EmptyState, ErrorState, LoadingState } from '@/components/states'
import { useToast } from '@/components/toastContext'
import { cn } from '@/lib/utils'
import { useBumpTicket, useKitchenTickets, useStations, type KitchenTicket } from './queries'

/**
 * How often a display asks again.
 *
 * **Matches `KitchenEndpoints.PollIntervalSeconds`.** There is no SSE and no
 * websocket anywhere in this project; adding a transport is its own phase with
 * its own reconnection, authentication and proxy problems, so the interval is a
 * stated, tunable number rather than an oversight. A screen refreshing every
 * five seconds is indistinguishable from a live one at the speed food is cooked.
 */
const POLL_MS = 5_000

/** Which station this screen is standing at. Survives a reload; it is not a credential. */
const STATION_KEY = 'pos.kitchen.station'

/**
 * The pass.
 *
 * **A kiosk, not a page.** It renders outside `AppLayout` — no nav, no header,
 * no tenant chrome — because a kitchen display is an appliance bolted to a wall
 * and read across a hot room at arm's length, not something a person navigates
 * to between other tasks. Everything is large, high contrast, and there is
 * exactly one action per ticket.
 *
 * **A voided line is struck through, never removed.** `isVoided` comes from the
 * order line's *current* status and is the only thing on a ticket allowed to
 * change after firing — the ticket itself is an append-only record of what the
 * kitchen was told. A chef may already have plated it, and a line that vanished
 * would erase the evidence that the shop lost one.
 */
export function KitchenPage() {
  const stations = useStations()
  const [stationId, setStationId] = useState<string | null>(() =>
    // sessionStorage, like everything else this app keeps: a shared screen hands
    // the next shift nothing, and this is a display preference rather than a
    // credential — invariant 11's rule is about what must *not* be stored.
    sessionStorage.getItem(STATION_KEY),
  )

  useEffect(() => {
    if (stationId === null) {
      sessionStorage.removeItem(STATION_KEY)
    } else {
      sessionStorage.setItem(STATION_KEY, stationId)
    }
  }, [stationId])

  if (stations.isPending) {
    return <LoadingState label="Reading the kitchen…" />
  }

  if (stations.isError) {
    return (
      <ErrorState
        title="Could not read the stations."
        error={stations.error}
        onRetry={stations.refetch}
      />
    )
  }

  const all = stations.data ?? []

  if (all.length === 0) {
    return (
      <EmptyState
        title="No stations yet."
        description="A manager adds the kitchen's stations before anything can be fired to them."
      />
    )
  }

  const station = all.find((candidate) => candidate.id === stationId) ?? null

  if (station === null) {
    return (
      <div className="flex h-full flex-col items-center justify-center gap-6 p-8">
        <h1 className="text-2xl font-semibold text-foreground">Which screen is this?</h1>

        <div className="flex flex-wrap justify-center gap-3">
          {all.map((candidate) => (
            <Button
              key={candidate.id}
              type="button"
              className="h-20 min-w-40 text-lg"
              onClick={() => setStationId(candidate.id)}
            >
              {candidate.name}
            </Button>
          ))}
        </div>

        <p className="text-sm text-muted-foreground">
          Remembered for this screen until it is closed.
        </p>
      </div>
    )
  }

  return (
    <Queue stationId={station.id} stationName={station.name} onChange={() => setStationId(null)} />
  )
}

/** One station's queue, oldest first. */
function Queue({
  stationId,
  stationName,
  onChange,
}: {
  stationId: string
  stationName: string
  onChange: () => void
}) {
  const [showBumped, setShowBumped] = useState(false)
  const tickets = useKitchenTickets(stationId, showBumped, POLL_MS)

  const rows = tickets.data ?? []

  return (
    <div className="flex h-full flex-col">
      <header className="flex items-center justify-between gap-4 border-b border-border px-4 py-3">
        <h1 className="text-xl font-bold tracking-wide text-foreground uppercase">{stationName}</h1>

        <div className="flex items-center gap-3">
          <span className="text-sm tabular-nums text-muted-foreground" data-testid="ticket-count">
            {rows.filter((ticket) => ticket.status === 'Active').length} up
          </span>

          <Button
            type="button"
            variant={showBumped ? 'default' : 'outline'}
            size="sm"
            onClick={() => setShowBumped((current) => !current)}
          >
            {showBumped ? 'Hide cleared' : 'Show cleared'}
          </Button>

          <Button type="button" variant="ghost" size="sm" onClick={onChange}>
            Change station
          </Button>
        </div>
      </header>

      {tickets.isPending ? (
        <LoadingState label="Reading the board…" />
      ) : rows.length === 0 ? (
        <EmptyState
          className="m-8"
          title="Nothing to cook."
          description="New rounds appear here."
        />
      ) : (
        // Columns rather than a list: a pass reads left to right and the oldest
        // table must never scroll off the bottom.
        <div className="flex flex-1 gap-3 overflow-x-auto p-3">
          {rows.map((ticket) => (
            <Ticket key={ticket.id} ticket={ticket} />
          ))}
        </div>
      )}
    </div>
  )
}

/** One ticket. */
function Ticket({ ticket }: { ticket: KitchenTicket }) {
  const toast = useToast()
  const bump = useBumpTicket()

  const bumped = ticket.status === 'Bumped'

  const act = async () => {
    try {
      await bump.mutateAsync({ ticketId: ticket.id, action: bumped ? 'recall' : 'bump' })
    } catch (caught) {
      toast.showError(caught, bumped ? 'Could not recall that.' : 'Could not clear that.')
    }
  }

  return (
    <section
      aria-label={`${ticket.orderLabel} course ${ticket.course}`}
      data-testid={`ticket-${ticket.orderNumber}-${ticket.course}`}
      className={cn(
        'flex w-64 shrink-0 flex-col gap-2 rounded-xl border-2 p-3',
        bumped ? 'border-border opacity-50' : 'border-primary/40 bg-card',
      )}
    >
      <div className="flex items-baseline justify-between gap-2">
        <span className="text-lg font-bold text-foreground">{ticket.orderLabel}</span>
        <Elapsed since={ticket.firedAt} />
      </div>

      <span className="text-xs font-medium tracking-wide text-muted-foreground uppercase">
        Course {ticket.course} · #{ticket.orderNumber}
      </span>

      <ul className="flex flex-1 flex-col gap-2">
        {ticket.lines.map((line) => (
          <li key={line.id} className="flex flex-col">
            <span
              className={cn(
                'text-base font-semibold text-foreground',
                // Struck, not removed. The chef may already have plated it.
                line.isVoided && 'text-muted-foreground line-through',
              )}
            >
              {line.quantity}× {line.description}
              {line.seatNumber === null ? null : (
                <span className="ml-2 text-xs font-normal text-muted-foreground">
                  seat {line.seatNumber}
                </span>
              )}
            </span>

            {line.modifierText === null ? null : (
              <span className="pl-4 text-sm text-muted-foreground">{line.modifierText}</span>
            )}

            {line.note === null ? null : (
              <span className="pl-4 text-sm font-semibold text-destructive">{line.note}</span>
            )}
          </li>
        ))}
      </ul>

      <Button
        type="button"
        variant={bumped ? 'outline' : 'default'}
        // Tall: this is hit with the side of a hand, often with a glove on.
        className="h-12 text-base"
        disabled={bump.isPending}
        onClick={() => void act()}
        data-testid={bumped ? 'recall' : 'bump'}
      >
        {bumped ? 'Recall' : 'Bump'}
      </Button>
    </section>
  )
}

/**
 * How long this has been up.
 *
 * Counts locally between polls, so the number moves every second rather than
 * jumping in five-second steps — the elapsed time is what a pass paces by, and a
 * stuttering clock reads as a frozen screen.
 */
function Elapsed({ since }: { since: string }) {
  const [now, setNow] = useState(() => Date.now())

  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), 1_000)
    return () => clearInterval(timer)
  }, [])

  const minutes = Math.max(0, Math.floor((now - new Date(since).getTime()) / 60_000))

  return (
    <span
      className={cn(
        'text-sm font-semibold tabular-nums',
        minutes >= 15 ? 'text-destructive' : 'text-muted-foreground',
      )}
    >
      {minutes}m
    </span>
  )
}
