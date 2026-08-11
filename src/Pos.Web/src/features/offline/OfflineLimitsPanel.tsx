/**
 * What offline on a browser actually promises, said in the app rather than only
 * in a document nobody reads.
 *
 * **The phase doc is blunt about why this exists, and it is worth repeating
 * here rather than paraphrasing:** overstating offline durability would be the
 * most damaging thing in the project. A shop that believes it is bulletproof,
 * trades all day on it, and loses the data will not come back — and will tell
 * people. Underpromising costs a feature bullet; overpromising costs the
 * business.
 *
 * So this panel exists to be *unflattering*. It says what can be lost, how, and
 * what the eventual fix is. Nothing on it claims a guarantee.
 */

import { useEffect, useState } from 'react'
import { Button } from '@/components/ui/button'
import { readStorageReport, requestPersistence, type StorageReport } from '@/lib/offline/persist'
import { useOffline } from './offlineContext'

export function OfflineLimitsPanel() {
  const offline = useOffline()
  const [report, setReport] = useState<StorageReport | null>(null)

  useEffect(() => {
    void readStorageReport().then(setReport)
  }, [])

  return (
    <section
      aria-label="Offline limits"
      data-testid="offline-limits"
      className="flex flex-col gap-4 rounded-xl border border-border bg-card p-4"
    >
      <div className="flex flex-col gap-1">
        <h2 className="text-sm font-semibold text-foreground">Offline on this till</h2>
        <p className="text-sm text-muted-foreground">
          This till keeps a copy of the catalog and can take cash sales while the connection is
          down. It is <strong>best effort</strong>, and the limits below are real.
        </p>
      </div>

      <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-sm">
        <dt className="text-muted-foreground">Connection</dt>
        <dd className="text-foreground">
          {offline.connectivity === 'offline' ? 'Cannot reach the server' : 'Connected'}
        </dd>

        <dt className="text-muted-foreground">Products stored</dt>
        <dd className="tabular-nums text-foreground">{offline.mirrorSize}</dd>

        <dt className="text-muted-foreground">Sales waiting to send</dt>
        <dd className="tabular-nums text-foreground">{offline.pendingSales}</dd>

        <dt className="text-muted-foreground">Storage</dt>
        <dd className="text-foreground">{describeStorage(report)}</dd>
      </dl>

      {report?.persistence === 'denied' || report?.persistence === 'unsupported' ? (
        <div className="flex flex-wrap items-center gap-3">
          <p className="flex-1 text-sm text-foreground">
            This browser has not promised to keep the till's data. Asking again sometimes helps once
            the device has been used for a while.
          </p>
          <Button
            variant="outline"
            size="sm"
            onClick={() => {
              void requestPersistence().then(() => readStorageReport().then(setReport))
            }}
          >
            Ask again
          </Button>
        </div>
      ) : null}

      {/* The part that must not be softened. */}
      <div className="flex flex-col gap-2 rounded-lg border border-destructive/30 bg-destructive/5 p-3 text-sm">
        <p className="font-medium text-foreground">What can go wrong</p>
        <ul className="flex list-disc flex-col gap-1 pl-5 text-muted-foreground">
          <li>
            <strong>Clearing this browser's data deletes any sale that has not been sent.</strong>{' '}
            There is no copy anywhere else and it cannot be recovered.
          </li>
          <li>
            Browsers may reclaim storage on their own when a device is short of space. iPhones and
            iPads are the strictest, and may clear it after a period of not being used.
          </li>
          <li>
            A till that is restarted while offline cannot sign in, so it cannot trade until the
            connection is back. Queued sales are kept and sent then.
          </li>
          <li>
            Extended offline trading on a browser risks losing sales. If the connection is going to
            be down for a long time, keep a written record as well.
          </li>
        </ul>
        <p className="text-muted-foreground">
          Full offline reliability needs a real local database, which is what the desktop
          application is for. This is the honest position for a browser.
        </p>
      </div>
    </section>
  )
}

function describeStorage(report: StorageReport | null): string {
  if (report === null) {
    return 'Checking…'
  }

  const size =
    report.usage === null
      ? ''
      : ` · using ${(report.usage / 1_048_576).toFixed(1)} MB${
          report.quota === null ? '' : ` of about ${(report.quota / 1_048_576).toFixed(0)} MB`
        }`

  switch (report.persistence) {
    case 'granted':
      return `The browser has agreed to keep it${size}`
    case 'denied':
      // Not softened into "may be cleared occasionally". It may be cleared.
      return `Not protected — the browser may clear it${size}`
    case 'unsupported':
      return `This browser cannot promise to keep it${size}`
    default:
      return `Unknown${size}`
  }
}
