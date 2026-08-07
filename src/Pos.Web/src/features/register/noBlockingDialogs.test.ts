import { describe, expect, it } from 'vitest'

/**
 * Every source file in the register and the receipt, as text.
 *
 * `import.meta.glob` rather than `node:fs`: `tsconfig.app.json` covers `src`
 * only and deliberately excludes Node's globals, because application code must
 * not be able to reach them. Vite resolves this at build time and it stays
 * inside the same project as the code it reads.
 *
 * `features/sales` joined the scan in 6.2. It is reached *from* the register —
 * the completion panel opens a receipt with a customer at the counter — so it is
 * the same screen as far as a stalled queue is concerned, and it is where the
 * temptation comes back: `confirm('Print this receipt?')` is a one-liner.
 */
const SOURCES = import.meta.glob(['./*.{ts,tsx}', '../sales/*.{ts,tsx}'], {
  query: '?raw',
  eager: true,
  import: 'default',
}) as Record<string, string>

/**
 * CLAUDE.md invariant 10, pinned rather than reviewed.
 *
 * `alert()`, `confirm()` and `prompt()` block the event loop. Behind one, a
 * wedge scanner keeps typing: the keystrokes queue and are replayed into
 * whatever has focus when the dialog closes, which at a till means a barcode
 * landing in a PIN field or a quantity box. The queue does not stop either way.
 *
 * 5.3 is where the temptation is strongest — a discount amount and a manager's
 * PIN are both exactly the sort of thing `prompt()` looks convenient for — so
 * the rule gets a test in the milestone that would have broken it. Both are
 * in-page dialogs instead: `LineAdjustDialog` and `ManagerAuthorizationDialog`.
 */
describe('the register and the receipt', () => {
  it('open no blocking browser dialogs', () => {
    const offenders: string[] = []

    for (const [file, contents] of Object.entries(SOURCES)) {
      if (/\.test\.tsx?$/.test(file)) {
        continue
      }

      const source = withoutComments(contents)

      // Word-boundary-anchored so `confirmLabel`, `onConfirm` and
      // `ConfirmButton` — the in-page replacement — are not false positives.
      for (const match of source.matchAll(/(?<![.\w])(alert|confirm|prompt)\s*\(/g)) {
        offenders.push(`${file}: ${match[1]!}()`)
      }
    }

    expect(offenders).toEqual([])
  })
})

/**
 * Strips comments before scanning.
 *
 * The files this guards are the ones most likely to *discuss* `prompt()` — the
 * doc comment on each dialog explains why it is not one. A scanner that read
 * prose would fail on the explanation of the rule it is enforcing, and the fix
 * for that failure would be to delete the explanation.
 */
function withoutComments(source: string): string {
  return source.replaceAll(/\/\*[\s\S]*?\*\//g, '').replaceAll(/\/\/.*$/gm, '')
}
