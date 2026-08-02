/**
 * Telling a barcode scanner from a person.
 *
 * A wedge scanner *is* a keyboard: it types the code and presses Enter. Nothing
 * in the event says which device sent it, so the only signal available is
 * **timing** — a scanner emits a whole code in tens of milliseconds, and a human
 * being cannot. Everything here follows from that one fact.
 *
 * The rule is a **budget over the whole sequence**, not a limit on each gap:
 * a scan must arrive within `maxIntervalMs` per character on average. Measured
 * per gap, one stalled frame — React rendering the previous scan, which is
 * exactly when the next one arrives — would split a code in two, and the tail
 * `099999000011` of a real code is a lookup that might even succeed against
 * something else. Measured as a budget, a 100ms stall inside a 13-character
 * burst is still obviously a machine, while a person typing the same string is
 * not, by an order of magnitude.
 *
 * What is *not* a scan is returned as `manual` — the same keystrokes are how a
 * cashier types a quantity, and one classifier deciding both is what stops a
 * scan and a keypad entry ever acting on the same characters.
 *
 * Deliberately pure: no DOM, no `Date.now()`, no timers. The caller supplies the
 * timestamp, so a test drives a hundred keystrokes with exact intervals instead
 * of sleeping, and behaviour on a slow CI runner is a decision rather than a
 * race. `useScanner` is the thin DOM layer over this.
 */

export interface ScannerOptions {
  /**
   * The average time per character a scan is allowed to take.
   *
   * A 13-character code therefore has 780ms to arrive. A wedge scanner uses
   * 30–60ms of that; sustained human typing at this pace would be 200 words a
   * minute, which is faster than the world record and could not be kept up for
   * a whole barcode.
   */
  maxIntervalMs: number
  /**
   * A pause long enough to mean the previous keystrokes were abandoned.
   *
   * After this, whatever was typed is dropped and a new sequence begins — so a
   * half-typed quantity does not end up glued to the front of the next code.
   * Comfortably longer than hunt-and-peck typing, so a slowly typed quantity is
   * still one number.
   */
  idleResetMs: number
  /**
   * The shortest thing that can be a barcode.
   *
   * EAN-8 is the shortest real symbology. Below this, a fast `12` and Enter is
   * far more likely to be someone entering a quantity.
   */
  minLength: number
  /**
   * How long the same code is ignored after it has been read.
   *
   * **Scanners genuinely double-fire**, and an unsuppressed double-fire is two
   * units sold. Measured from the previous scan to the moment the next one
   * *starts*, so how long a code takes to transmit does not eat the window.
   *
   * Short on purpose: a cashier deliberately scanning two identical items takes
   * far longer than this, and a window wide enough to swallow that would
   * silently lose a sale line — which is worse, because nobody notices until
   * the stocktake.
   */
  duplicateWindowMs: number
}

export const DEFAULT_SCANNER_OPTIONS: ScannerOptions = {
  maxIntervalMs: 60,
  idleResetMs: 1000,
  minLength: 4,
  duplicateWindowMs: 300,
}

/**
 * What barcodes look like here.
 *
 * Letters and digits only — which also stops `0.35`, typed quickly into the
 * keypad, being mistaken for a four-character code, since a barcode never
 * contains a decimal point.
 */
const CODE_PATTERN = /^[0-9A-Za-z]+$/

/**
 * What a completed keystroke sequence turned out to be.
 *
 * `manual` is not a failure — it is the *other* answer, and the register uses it
 * as keypad input.
 */
export type ScanResult =
  | { kind: 'scan'; code: string }
  | { kind: 'duplicate'; code: string }
  | { kind: 'manual'; text: string }

export interface ScanClassifier {
  /**
   * Feeds one keystroke in. Returns a result only when the sequence ends, which
   * is on Enter.
   */
  handle: (key: string, timestamp: number) => ScanResult | null
  /** Drops the sequence — on blur, on Escape, or when the register stands down. */
  reset: () => void
  /** What has been typed since the last commit, for a live keypad display. */
  pending: () => string
}

export function createScanClassifier(options: Partial<ScannerOptions> = {}): ScanClassifier {
  const { maxIntervalMs, idleResetMs, minLength, duplicateWindowMs } = {
    ...DEFAULT_SCANNER_OPTIONS,
    ...options,
  }

  /** Everything typed since the last commit or idle reset. */
  let buffer = ''
  /** When the first character of the current sequence arrived. */
  let startedAt = 0
  /** Timestamp of the previous character, for the idle test. */
  let lastKeyAt: number | null = null
  /** The last accepted scan, for the double-fire guard. */
  let lastCode: { code: string; at: number } | null = null

  function clear(): void {
    buffer = ''
    lastKeyAt = null
  }

  /** Whether what was typed arrived faster than a person could have typed it. */
  function isMachinePaced(endedAt: number): boolean {
    return endedAt - startedAt <= buffer.length * maxIntervalMs
  }

  return {
    handle(key, timestamp) {
      if (key === 'Enter') {
        const text = buffer
        const paced = isMachinePaced(timestamp)
        clear()

        if (text === '') {
          // A bare Enter is a plain keypress; the caller's key map owns it.
          return null
        }

        if (!paced || text.length < minLength || !CODE_PATTERN.test(text)) {
          return { kind: 'manual', text }
        }

        // Measured to the *start* of this burst, so a scanner firing the same
        // code twice in quick succession adds one unit however long the code
        // takes to send.
        const isRepeat =
          lastCode !== null && lastCode.code === text && startedAt - lastCode.at < duplicateWindowMs

        lastCode = { code: text, at: startedAt }

        return isRepeat ? { kind: 'duplicate', code: text } : { kind: 'scan', code: text }
      }

      if (key === 'Backspace') {
        // Editing a keypad entry. A scanner does not send Backspace, so this
        // also means whatever is left is not going to be read as a code — the
        // pacing test below will have long since failed by the time it matters.
        buffer = buffer.slice(0, -1)
        lastKeyAt = timestamp
        return null
      }

      // Anything that is not a single printable character ends the sequence: a
      // scanner sends characters and one Enter, so a Tab, an arrow or a function
      // key means a person is doing something else entirely.
      if (key.length !== 1) {
        clear()
        return null
      }

      // A long enough pause means the earlier keystrokes were abandoned — a
      // half-typed quantity, then the cashier reached for the scanner.
      if (lastKeyAt === null || timestamp - lastKeyAt > idleResetMs) {
        buffer = key
        startedAt = timestamp
        lastKeyAt = timestamp
        return null
      }

      buffer += key
      lastKeyAt = timestamp
      return null
    },

    reset: clear,

    pending: () => buffer,
  }
}

/**
 * Whether a keystroke landing on this element belongs to the element.
 *
 * The scanner must **stand down while a text field has focus**, or a cashier
 * typing into the product search would have their letters stolen into a barcode
 * buffer. The register's own keyboard shortcuts follow the same rule, which is
 * why this lives here rather than in the hook.
 */
export function isEditableTarget(target: EventTarget | null): boolean {
  if (!(target instanceof HTMLElement)) {
    return false
  }

  if (target.isContentEditable) {
    return true
  }

  const tag = target.tagName

  return tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT'
}
