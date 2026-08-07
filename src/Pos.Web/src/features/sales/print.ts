/**
 * Asking the browser to print.
 *
 * A one-line module so that the call is a seam a test can stub. Nothing in the
 * suites may reach the real one: `window.print()` opens a modal the page cannot
 * dismiss, and a Playwright run that triggered it would hang exactly the way a
 * stray `alert()` would.
 */

/**
 * Prints the current document.
 *
 * **This blocks the tab, and that is accepted here** — narrowly. CLAUDE.md
 * invariant 10 forbids `alert`, `confirm` and `prompt` because they stall a
 * queue while a scanner keeps typing into whatever has focus afterwards. The
 * print dialog blocks the same way, but the two differ where it matters: a
 * cashier pressed a button that says Print and is now looking at the printer,
 * rather than being interrupted mid-scan by a dialog the application chose to
 * open.
 *
 * The rule that follows from it: **nothing auto-prints.** Printing is always the
 * result of a press, never of a sale completing, a route loading or a receipt
 * query resolving.
 */
export function printPaper(): void {
  window.print()
}
