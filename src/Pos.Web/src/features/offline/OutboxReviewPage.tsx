/**
 * Sales this till took that the server refused.
 *
 * **The queue's only exit that is not "keep trying".** Without this screen a
 * permanently-refused sale is a black hole: the till would report it as pending
 * for ever, a cashier would read that as "it will sort itself out", and the
 * first anybody would hear of it is a drawer that does not balance.
 *
 * Each row answers three questions in order, because that is the order a
 * manager asks them:
 *
 * 1. **What was sold, and when** — including how long ago it was rung, since a
 *    sale from Tuesday evening is a different conversation from one from an
 *    hour ago.
 * 2. **What it means for the money** — `describeRefusal` names the consequence
 *    rather than the status code.
 * 3. **What to do** — one action, or an honest statement that this screen
 *    cannot fix it.
 *
 * `CanCloseShift`, matching the Z-report and the shift close: this is
 * reconciliation, and the person who decides what the drawer should contain is
 * the person who reconciles it.
 */

import { useCallback, useEffect, useState } from 'react'
import { useAuth } from '@/auth/authContext'
import { Button } from '@/components/ui/button'
import { ConfirmButton } from '@/components/ConfirmButton'
import { EmptyState } from '@/components/states'
import { useToast } from '@/components/toastContext'
import { useCurrentShift } from '@/features/register/queries'
import type { OutboxSale } from '@/lib/offline/db'
import { discard, listForReview, refile } from '@/lib/offline/outbox'
import { attempt } from '@/lib/offline/replay'
import { OfflineLimitsPanel } from './OfflineLimitsPanel'
import { useOffline } from './offlineContext'
import { describeRefusal } from './refusal'

export function OutboxReviewPage() {
  const { tenant } = useAuth()
  const offline = useOffline()
  const shift = useCurrentShift()
  const toast = useToast()

  const [rows, setRows] = useState<OutboxSale[] | null>(null)
  const [busy, setBusy] = useState<string | null>(null)

  const load = useCallback(async () => {
    if (offline.db === null) {
      setRows([])
      return
    }

    setRows(await listForReview(offline.db))
  }, [offline.db])

  useEffect(() => {
    void load()
  }, [load])

  /**
   * Files the sale against the drawer that is open now.
   *
   * **A new idempotency key, and the original `occurredAt`.** The body changes
   * — a different `shiftId` — so it is genuinely new work and the old key is
   * spent; reusing it would be answered `409 idempotency-key-reused`, correctly.
   * The timestamp is carried over unchanged because the trade happened when it
   * happened, and re-dating it would move a sale into a trading day it did not
   * belong to.
   *
   * The previous shift's variance is left exactly as it was. Phase 6.3's rule
   * stands: a closed shift's variance is read, never recomputed. It is the
   * record of the anomaly, and this action does not erase it.
   */
  const onRefile = useCallback(
    async (record: OutboxSale) => {
      if (offline.db === null || shift.data === undefined) {
        return
      }

      setBusy(record.saleKey)

      try {
        const saleKey = crypto.randomUUID()

        const body = {
          ...(record.body as Record<string, unknown>),
          clientTransactionId: saleKey,
          shiftId: shift.data.id,
        }

        await refile(offline.db, record.saleKey, { saleKey, shiftId: shift.data.id, body })

        // Sent immediately rather than left for the timer: somebody is standing
        // here having just made a decision, and they should see the outcome.
        const outcome = await attempt({ ...record, saleKey, shiftId: shift.data.id, body })

        if (outcome.kind === 'accepted') {
          await discard(offline.db, saleKey)
          toast.show('Filed against the open drawer.', {
            tone: 'success',
            detail: `Sale #${String(outcome.sale.saleNumber ?? '—')} is now recorded.`,
          })
        } else {
          toast.show('It was refused again.', {
            tone: 'error',
            detail: outcome.kind === 'review' ? outcome.reason : 'The server could not be reached.',
          })
        }

        await offline.refresh()
        await load()
      } finally {
        setBusy(null)
      }
    },
    [load, offline, shift.data, toast],
  )

  const onDiscard = useCallback(
    async (record: OutboxSale) => {
      if (offline.db === null) {
        return
      }

      await discard(offline.db, record.saleKey)
      await offline.refresh()
      await load()

      toast.show('Removed from this till.', {
        tone: 'info',
        detail: 'The sale is gone from the queue and was never recorded on the server.',
      })
    },
    [load, offline, toast],
  )

  return (
    <div className="flex flex-col gap-4 p-4">
      <header className="flex flex-col gap-1">
        <h1 className="text-lg font-semibold text-foreground">Sales needing review</h1>
        <p className="max-w-3xl text-sm text-muted-foreground">
          These were taken on this till and refused by the server.{' '}
          <strong>The money was collected</strong> — none of them is recorded until somebody here
          decides what to do with it.
        </p>
      </header>

      {offline.pendingSales > 0 ? (
        <p
          role="status"
          className="rounded-lg border border-border bg-muted px-4 py-2 text-sm text-foreground"
        >
          {offline.pendingSales} more {offline.pendingSales === 1 ? 'sale is' : 'sales are'} still
          waiting to send. Those are fine — they are queued, not refused.
        </p>
      ) : null}

      {/* The limits sit on this screen because this is where somebody comes when
          the queue has gone wrong, and it is the moment they most need to know
          what the till does and does not promise. */}
      <OfflineLimitsPanel />

      {rows === null ? null : rows.length === 0 ? (
        <EmptyState
          title="Nothing needs review."
          description="Every sale this till has taken has reached the server."
        />
      ) : (
        <ul className="flex flex-col gap-3">
          {rows.map((record) => (
            <ReviewRow
              key={record.saleKey}
              record={record}
              currency={tenant?.currencyCode ?? 'GBP'}
              busy={busy === record.saleKey}
              canRefile={shift.isSuccess}
              onRefile={() => {
                void onRefile(record)
              }}
              onDiscard={() => {
                void onDiscard(record)
              }}
            />
          ))}
        </ul>
      )}
    </div>
  )
}

function ReviewRow({
  record,
  currency,
  busy,
  canRefile,
  onRefile,
  onDiscard,
}: {
  record: OutboxSale
  currency: string
  busy: boolean
  canRefile: boolean
  onRefile: () => void
  onDiscard: () => void
}) {
  const refusal = describeRefusal(record.lastErrorType, record.lastError)

  return (
    <li
      data-testid="review-row"
      className={
        refusal.severity === 'danger'
          ? 'flex flex-col gap-3 rounded-xl border border-destructive/40 bg-destructive/5 p-4'
          : 'flex flex-col gap-3 rounded-xl border border-amber-500/40 bg-amber-500/5 p-4'
      }
    >
      <div className="flex flex-wrap items-baseline justify-between gap-2">
        <span className="text-sm font-semibold text-foreground">
          {money(record.display.total, currency)} · {record.display.lines.length}{' '}
          {record.display.lines.length === 1 ? 'item' : 'items'}
        </span>

        <span className="text-xs text-muted-foreground">
          Rung {formatWhen(record.occurredAt)} · Ref {record.saleKey.slice(0, 8)}
        </span>
      </div>

      <div className="flex flex-col gap-1">
        <p className="text-sm font-medium text-foreground">{refusal.summary}</p>
        <p className="text-sm text-muted-foreground">{refusal.consequence}</p>
      </div>

      <ul className="flex flex-col gap-0.5 text-sm text-muted-foreground">
        {record.display.lines.map((line, index) => (
          <li key={index} className="flex justify-between gap-3">
            <span className="truncate">
              {line.quantity} × {line.description}
            </span>
            <span className="tabular-nums">{money(line.lineTotal, currency)}</span>
          </li>
        ))}
      </ul>

      <div className="flex flex-wrap items-center gap-2">
        {refusal.remedy === 'refile' ? (
          <Button size="sm" disabled={busy || !canRefile} onClick={onRefile}>
            {busy ? 'Filing…' : 'File against the open drawer'}
          </Button>
        ) : null}

        {refusal.remedy === 'refile' && !canRefile ? (
          <span className="text-xs text-muted-foreground">
            Open a drawer on this till first — that is what it would be filed against.
          </span>
        ) : null}

        {/* Destructive and irreversible, so it asks in the page rather than
            through a blocking dialog (invariant 10) and says what is lost. */}
        <ConfirmButton confirmLabel="Remove — the sale will not be recorded" onConfirm={onDiscard}>
          Remove from this till
        </ConfirmButton>
      </div>
    </li>
  )
}

/**
 * When the sale was rung, in plain words.
 *
 * The elapsed time rather than only a timestamp: a manager reconciling at the
 * end of a shift needs to know whether this is from an hour ago or from
 * Tuesday, and "17:42" does not answer that.
 */
function formatWhen(iso: string, now: number = Date.now()): string {
  const at = Date.parse(iso)

  if (Number.isNaN(at)) {
    return 'at an unknown time'
  }

  const hours = Math.floor((now - at) / 3_600_000)
  const stamp = new Date(at).toLocaleString(undefined, {
    dateStyle: 'short',
    timeStyle: 'short',
  })

  if (hours < 1) {
    return `less than an hour ago (${stamp})`
  }

  if (hours < 24) {
    return `${String(hours)}h ago (${stamp})`
  }

  return `${String(Math.floor(hours / 24))} days ago (${stamp})`
}

function money(amount: string, currency: string): string {
  return new Intl.NumberFormat(undefined, { style: 'currency', currency }).format(Number(amount))
}
