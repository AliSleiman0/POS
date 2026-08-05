import { render } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { useScanner } from './useScanner'

/**
 * When the scanner stands aside.
 *
 * The global listener is the whole point of 5.2 — staff will not click into a
 * field before scanning — but "global" has to stop somewhere, and the boundary
 * is not obvious enough to leave to review. Both cases here are ones a real
 * scan, or a real person, walks into.
 */
describe('useScanner', () => {
  function Harness({ onScan }: { onScan: (code: string) => void }) {
    useScanner(true, { onScan })
    return null
  }

  /** A wedge scanner's burst: characters in one turn, then Enter. */
  function burst(code: string, target: EventTarget = document): void {
    for (const character of code) {
      target.dispatchEvent(new KeyboardEvent('keydown', { key: character, bubbles: true }))
    }
    target.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }))
  }

  afterEach(() => {
    document.body.innerHTML = ''
  })

  it('reads a scan when nothing is in the way', () => {
    // The positive control. Without it, everything below would pass against a
    // listener that had simply stopped working.
    const onScan = vi.fn()

    render(<Harness onScan={onScan} />)
    burst('5099999000011')

    expect(onScan).toHaveBeenCalledWith('5099999000011')
  })

  it('stands down while a modal dialog is open', () => {
    /*
     * Found by driving the register, not by reasoning about it.
     *
     * In the manager-authorisation dialog, selecting a name puts focus on a
     * *button*. A PIN typed before focus reaches the input therefore has a
     * non-editable target — so the scanner read it, and `7391` is four digits,
     * which clears `minLength`. The register looked it up as a barcode and
     * printed the manager's PIN back on screen in the unknown-item banner.
     *
     * `ReauthOverlay` had the identical hole and nobody had noticed.
     */
    const onScan = vi.fn()

    render(<Harness onScan={onScan} />)

    const dialog = document.createElement('div')
    dialog.setAttribute('role', 'dialog')
    dialog.setAttribute('aria-modal', 'true')
    document.body.append(dialog)

    const button = document.createElement('button')
    dialog.append(button)

    burst('7391', button)

    expect(onScan).not.toHaveBeenCalled()
  })

  it('reads scans again once the dialog closes', () => {
    // The stand-down must be a pause, not a stop: the next customer's items go
    // through the same listener.
    const onScan = vi.fn()

    render(<Harness onScan={onScan} />)

    const dialog = document.createElement('div')
    dialog.setAttribute('aria-modal', 'true')
    document.body.append(dialog)

    burst('5099999000011')
    expect(onScan).not.toHaveBeenCalled()

    dialog.remove()

    burst('5099999000011')
    expect(onScan).toHaveBeenCalledWith('5099999000011')
  })

  it('stands down for a focused text field', () => {
    // The 5.2 rule, kept under test beside the one it turned out not to cover.
    const onScan = vi.fn()

    render(<Harness onScan={onScan} />)

    const input = document.createElement('input')
    document.body.append(input)

    burst('5099999000011', input)

    expect(onScan).not.toHaveBeenCalled()
  })
})
