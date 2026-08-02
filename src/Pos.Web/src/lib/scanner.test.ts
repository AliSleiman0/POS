import { describe, expect, it } from 'vitest'
import { createScanClassifier, DEFAULT_SCANNER_OPTIONS, type ScanResult } from './scanner'

/**
 * The scanner's timing rules.
 *
 * This is the milestone that cannot be finished by tests alone — "does it fight
 * manual entry in practice" is a judgement made by a person at a keyboard — but
 * the arithmetic underneath it is exactly the kind of thing that is easy to get
 * wrong and impossible to reproduce by clicking: a double-fire is two units
 * sold, a code stolen from a search box is a cashier fighting the till, and a
 * code split in half is a lookup for a product nobody scanned.
 */
describe('createScanClassifier', () => {
  /**
   * Types a string at a given pace and returns whatever the Enter produced.
   *
   * `at` is the clock the classifier is given, so intervals are exact rather
   * than whatever the runner felt like doing.
   */
  function type(
    classifier: ReturnType<typeof createScanClassifier>,
    text: string,
    { intervalMs, at = 1000 }: { intervalMs: number; at?: number },
  ): { result: ScanResult | null; at: number } {
    let clock = at

    for (const character of text) {
      classifier.handle(character, clock)
      clock += intervalMs
    }

    return { result: classifier.handle('Enter', clock), at: clock }
  }

  it('reads a machine-paced burst as a scan', () => {
    const classifier = createScanClassifier()

    const { result } = type(classifier, '5099999000011', { intervalMs: 3 })

    expect(result).toEqual({ kind: 'scan', code: '5099999000011' })
  })

  it('survives one stalled frame in the middle of a code', () => {
    const classifier = createScanClassifier()

    // The stall is real and was observed: the frame that renders the *previous*
    // scan is running exactly when the next one arrives. Per-gap thresholding
    // split the code here and looked up `099999000011`, which is not what
    // anybody scanned — and might match something else.
    classifier.handle('5', 1000)
    classifier.handle('0', 1100)
    let clock = 1104

    for (const character of '99999000011') {
      classifier.handle(character, clock)
      clock += 3
    }

    expect(classifier.handle('Enter', clock)).toEqual({
      kind: 'scan',
      code: '5099999000011',
    })
  })

  it('reads human-paced typing as manual entry, not a barcode', () => {
    const classifier = createScanClassifier()

    // 120ms per character is brisk typing. Nothing here may reach the cart as a
    // product: a cashier entering a quantity is not scanning.
    const { result } = type(classifier, '0.35', { intervalMs: 120 })

    expect(result).toEqual({ kind: 'manual', text: '0.35' })
  })

  it('will not read a whole code typed by hand as a scan', () => {
    const classifier = createScanClassifier()

    // No single gap here is dramatic, but 13 characters at 100ms each is 1.3
    // seconds — an order of magnitude past any scanner, and the budget is what
    // notices that.
    const { result } = type(classifier, '5099999000011', { intervalMs: 100 })

    expect(result).toEqual({ kind: 'manual', text: '5099999000011' })
  })

  it('keeps a slowly typed quantity in one piece', () => {
    const classifier = createScanClassifier()

    // Hunt-and-peck on a counter tablet. Losing the leading digit here would
    // charge for 5 of something instead of 25.
    const { result } = type(classifier, '25', { intervalMs: 800 })

    expect(result).toEqual({ kind: 'manual', text: '25' })
  })

  it('suppresses a double-fire of the same code', () => {
    const classifier = createScanClassifier()

    const first = type(classifier, '5099999000011', { intervalMs: 3 })
    // Scanners really do fire twice. 40ms later is the device, not a person.
    const second = type(classifier, '5099999000011', { intervalMs: 3, at: first.at + 40 })

    expect(first.result).toEqual({ kind: 'scan', code: '5099999000011' })
    expect(second.result).toEqual({ kind: 'duplicate', code: '5099999000011' })
  })

  it('accepts the same code again once the window has passed', () => {
    const classifier = createScanClassifier()

    const first = type(classifier, '5099999000011', { intervalMs: 3 })
    // Two identical items, scanned one after the other. Suppressing this would
    // silently undercharge — and nobody notices until the stocktake.
    const second = type(classifier, '5099999000011', {
      intervalMs: 3,
      at: first.at + DEFAULT_SCANNER_OPTIONS.duplicateWindowMs + 1,
    })

    expect(second.result).toEqual({ kind: 'scan', code: '5099999000011' })
  })

  it('does not treat a short fast entry as a code', () => {
    const classifier = createScanClassifier()

    const { result } = type(classifier, '12', { intervalMs: 3 })

    expect(result).toEqual({ kind: 'manual', text: '12' })
  })

  it('does not treat a fast decimal quantity as a code', () => {
    const classifier = createScanClassifier()

    // Four characters typed quickly, but a barcode never contains a point.
    const { result } = type(classifier, '0.35', { intervalMs: 5 })

    expect(result).toEqual({ kind: 'manual', text: '0.35' })
  })

  it('lets a scan win over an abandoned keypad entry', () => {
    const classifier = createScanClassifier()

    // The cashier started typing a quantity, thought better of it, and scanned
    // the next item. The stray "5" must not end up glued to the front of the
    // code — after the idle pause it is simply gone.
    classifier.handle('5', 1000)

    const { result } = type(classifier, '5099999000011', {
      intervalMs: 3,
      at: 1000 + DEFAULT_SCANNER_OPTIONS.idleResetMs + 1,
    })

    expect(result).toEqual({ kind: 'scan', code: '5099999000011' })
  })

  it('ends the sequence on a key a scanner never sends', () => {
    const classifier = createScanClassifier()

    classifier.handle('5', 1000)
    classifier.handle('0', 1003)
    classifier.handle('ArrowDown', 1006)

    expect(classifier.handle('Enter', 1009)).toBeNull()
    expect(classifier.pending()).toBe('')
  })

  it('edits the pending entry on Backspace', () => {
    const classifier = createScanClassifier()

    classifier.handle('2', 2000)
    classifier.handle('5', 2200)
    classifier.handle('Backspace', 2400)

    expect(classifier.pending()).toBe('2')
    expect(classifier.handle('Enter', 2600)).toEqual({ kind: 'manual', text: '2' })
  })

  it('reports nothing for a bare Enter', () => {
    const classifier = createScanClassifier()

    // The register's own key map owns this one — Enter on an empty keypad is
    // not a quantity of nothing.
    expect(classifier.handle('Enter', 1000)).toBeNull()
  })

  it('forgets everything on reset', () => {
    const classifier = createScanClassifier()

    classifier.handle('5', 1000)
    classifier.handle('0', 1003)
    classifier.reset()

    expect(classifier.pending()).toBe('')
    expect(classifier.handle('Enter', 1006)).toBeNull()
  })
})
