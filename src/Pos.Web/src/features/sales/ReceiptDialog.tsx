import { useEffect, useState } from 'react'
import { createPortal } from 'react-dom'
import { Button } from '@/components/ui/button'
import { ErrorState, LoadingState } from '@/components/states'
import { Receipt, type Paper } from './Receipt'
import { printPaper } from './print'
import { useReceipt } from './queries'

/**
 * A receipt, previewed and printed.
 *
 * **In-page, never a browser dialog** (CLAUDE.md invariant 10). It renders
 * through a portal onto `document.body` rather than inside the register's tree,
 * for a reason that is about printing rather than about layering: the print
 * stylesheet hides `#root` outright, so the paper has to be outside it. That
 * also means what is previewed is the element that prints — one render, so a
 * preview cannot drift from the output.
 *
 * Escape closes it. Printing is a press and only a press: nothing here fires
 * `window.print()` on mount, because a till that opened a modal print dialog by
 * itself would stall a queue with a scanner still typing behind it.
 */
export function ReceiptDialog({ saleId, onClose }: { saleId: string; onClose: () => void }) {
  const receipt = useReceipt(saleId)
  const [paper, setPaper] = useState<Paper>('80mm')

  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        onClose()
      }
    }

    window.addEventListener('keydown', onKey)
    return () => {
      window.removeEventListener('keydown', onKey)
    }
  }, [onClose])

  return createPortal(
    <div className="receipt-overlay" role="dialog" aria-modal="true" aria-label="Receipt">
      {/* Chrome, not paper. `print-hide` keeps every one of these off the roll. */}
      <div className="print-hide flex flex-wrap items-center justify-center gap-2">
        <Button
          size="sm"
          disabled={!receipt.isSuccess}
          onClick={() => {
            printPaper()
          }}
        >
          Print
        </Button>
        <Button
          variant="outline"
          size="sm"
          aria-pressed={paper === 'a4'}
          onClick={() => {
            // The fallback for a shop printing to the office laser it already
            // owns, before it buys a thermal printer.
            setPaper((current) => (current === 'a4' ? '80mm' : 'a4'))
          }}
        >
          {paper === 'a4' ? 'A4 paper' : '80mm roll'}
        </Button>
        <Button variant="ghost" size="sm" onClick={onClose}>
          Close
        </Button>
      </div>

      {receipt.isPending ? (
        <div className="print-hide rounded-lg bg-background px-6">
          <LoadingState label="Fetching the receipt…" />
        </div>
      ) : receipt.isError ? (
        <div className="print-hide max-w-md rounded-lg bg-background px-6">
          <ErrorState
            error={receipt.error}
            title="That receipt could not be fetched."
            onRetry={() => {
              void receipt.refetch()
            }}
          />
        </div>
      ) : (
        // The server's answer, not the caller's. Until Phase 7.2 each call site
        // declared whether its copy was a reprint, so a client that simply omitted
        // the flag printed an unmarked duplicate — the refund-fraud vector §6.2
        // recorded. It is now derived from append-only entries nobody can suppress.
        <Receipt receipt={receipt.data} isReprint={receipt.data.isReprint} paper={paper} />
      )}
    </div>,
    document.body,
  )
}
