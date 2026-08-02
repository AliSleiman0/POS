/**
 * The DOM half of the scanner: one global `keydown` listener over the pure
 * classifier in `lib/scanner.ts`.
 *
 * **Global, because staff will not click into a field first.** A register that
 * requires focus in the right box before it accepts a scan is a register that
 * gets a scan into the wrong box, and the code ends up in the product search or
 * — worse — in a quantity.
 *
 * The listener stands down whenever a text field, textarea or select has focus.
 * That is what keeps manual entry working: while somebody is typing into the
 * product search, the keystrokes belong to the search.
 */

import { useEffect, useRef } from 'react'
import { createScanClassifier, isEditableTarget, type ScanResult } from '@/lib/scanner'

export interface ScannerHandlers {
  /** A complete, fresh code. */
  onScan: (code: string) => void
  /** The same code again inside the double-fire window — already sold, ignore. */
  onDuplicate?: (code: string) => void
  /** Enter on something that was typed rather than scanned: keypad input. */
  onManual?: (text: string) => void
  /** Any other key, once the classifier has decided it is not part of a code. */
  onKey?: (event: KeyboardEvent) => void
  /** What has been typed so far, for a live keypad display. */
  onPendingChange?: (pending: string) => void
  /**
   * Printable characters that are shortcuts rather than input.
   *
   * Only for characters a barcode and a quantity both exclude — `+` and `−`.
   * Anything else reserved here would be a character the scanner could send,
   * and it would go missing from the middle of a code.
   */
  reservedKeys?: Record<string, () => void>
}

/**
 * Listens for scans for as long as `enabled` is true.
 *
 * Disabled while the session is not live: a scan behind the re-auth overlay must
 * be dropped, not queued into a cart nobody can see.
 */
export function useScanner(enabled: boolean, handlers: ScannerHandlers): void {
  // The classifier keeps timing state across events, so it must not be rebuilt
  // on every render — that would reset the buffer mid-scan.
  const classifier = useRef(createScanClassifier())

  // Held in a ref so a changing callback (they close over the cart, which
  // changes on every scan) does not tear down and re-attach the listener.
  const latest = useRef(handlers)
  latest.current = handlers

  useEffect(() => {
    // Copied into the effect: the cleanup below must reset the classifier this
    // listener was using, not whatever the ref points at by then.
    const scan = classifier.current

    if (!enabled) {
      scan.reset()
      return
    }

    function onKeyDown(event: KeyboardEvent): void {
      // Someone is typing into a field. Their keystrokes are theirs.
      if (isEditableTarget(event.target)) {
        scan.reset()
        return
      }

      // A shortcut (Ctrl-R, Alt-Tab) is never part of a barcode, and swallowing
      // it would break the browser.
      if (event.ctrlKey || event.metaKey || event.altKey) {
        return
      }

      const reserved = latest.current.reservedKeys?.[event.key]

      if (reserved !== undefined) {
        // Never fed to the classifier: a `+` in the middle of a buffer would be
        // a character the code did not have.
        event.preventDefault()
        reserved()
        return
      }

      const result: ScanResult | null = scan.handle(event.key, event.timeStamp)

      latest.current.onPendingChange?.(scan.pending())

      if (result === null) {
        // Not the end of a sequence. The key map gets a look at it, but only
        // for keys that could not be part of a code — a digit belongs to the
        // buffer, and acting on it here would double-handle it.
        if (event.key.length !== 1) {
          latest.current.onKey?.(event)
        }
        return
      }

      // The sequence ended here, so nothing else may act on this Enter.
      event.preventDefault()

      switch (result.kind) {
        case 'scan':
          latest.current.onScan(result.code)
          break
        case 'duplicate':
          latest.current.onDuplicate?.(result.code)
          break
        case 'manual':
          latest.current.onManual?.(result.text)
          break
      }
    }

    document.addEventListener('keydown', onKeyDown)

    return () => {
      document.removeEventListener('keydown', onKeyDown)
      scan.reset()
    }
  }, [enabled])
}
