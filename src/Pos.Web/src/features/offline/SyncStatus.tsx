/**
 * What the till knows about itself: whether it can reach the server, how many
 * sales it still owes, and how old its prices are.
 *
 * **Visible at all times, and that is the requirement rather than a nicety.**
 * The phase doc puts it plainly: a cashier must never wonder whether a sale
 * saved, because that uncertainty is worse than a visible failure. A till that
 * silently queues is one where the first anybody hears of a problem is a
 * customer disputing a charge that was never taken.
 *
 * Three facts, and each answers a question somebody actually asks:
 *
 * | Shown | The question |
 * |---|---|
 * | Offline / Online | "Is it me or is it the shop's internet?" |
 * | *n* to send | "Did that sale go through?" |
 * | Prices as of 09:14 | "Can I trust this price?" |
 *
 * The mirror's age is the one that is easy to leave out and matters most while
 * things are working. A silent stale cache lets a till sell yesterday's prices
 * with total confidence; a visible one lets staff decide.
 */

import { Link } from 'react-router'
import { cn } from '@/lib/utils'
import { describeMirror } from './mirrorAge'
import { useOffline } from './offlineContext'

export function SyncStatus() {
  const { connectivity, pendingSales, reviewSales, mirroredAt, syncing, sync } = useOffline()

  const offline = connectivity === 'offline'

  return (
    <div className="flex items-center gap-2" data-testid="sync-status">
      <button
        type="button"
        // Tapping it asks now rather than waiting for the timer — the thing a
        // cashier reaches for the moment the queue stops moving.
        onClick={() => {
          void sync()
        }}
        title={
          offline
            ? 'This till cannot reach the server. Sales are being saved here.'
            : 'Connected. Tap to sync now.'
        }
        className={cn(
          'rounded-full px-2 py-0.5 text-xs font-medium transition-colors',
          offline
            ? 'bg-destructive/10 text-destructive'
            : connectivity === 'unknown'
              ? 'bg-muted text-muted-foreground'
              : 'bg-muted text-foreground',
        )}
      >
        {syncing ? 'Syncing…' : offline ? 'Offline' : connectivity === 'unknown' ? '…' : 'Online'}
      </button>

      {pendingSales > 0 ? (
        <span
          data-testid="pending-sales"
          // Not muted: this is money the shop has taken and the server has not
          // got. It should read as something outstanding, not as chrome.
          className="rounded-full bg-amber-500/15 px-2 py-0.5 text-xs font-semibold tabular-nums text-amber-700 dark:text-amber-400"
          title="Sales saved on this till and not yet sent."
        >
          {pendingSales} to send
        </span>
      ) : null}

      {reviewSales > 0 ? (
        // A link, because unlike the count above there is something a person
        // has to go and do about it.
        <Link
          to="/admin/sync"
          data-testid="review-sales"
          className="rounded-full bg-destructive/15 px-2 py-0.5 text-xs font-semibold tabular-nums text-destructive hover:bg-destructive/25"
          title="Sales the server refused. These need a manager."
        >
          {reviewSales} need review
        </Link>
      ) : null}

      <span className="text-xs text-muted-foreground" title="When the catalog was last updated.">
        {describeMirror(mirroredAt, offline)}
      </span>
    </div>
  )
}
