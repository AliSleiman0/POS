import { expect, test, type Page } from '@playwright/test'
import { enrolDevice, signIn } from './fixtures/actors'
import { RECEIPT_FOOTER } from './fixtures/seed'

/**
 * The receipt, end to end against a real API and a real Postgres.
 *
 * Phase 6.2. What is being proved here is the join between the two halves: the
 * server renders a payload from a sale's own rows, and the till shows that
 * payload — the same element that would go to the printer.
 *
 * **Nothing in this file calls `window.print()`.** The print dialog is modal and
 * the page cannot dismiss it, so a run that opened one would hang exactly the
 * way a stray `alert()` would. The button is asserted present and enabled; what
 * comes out of it is a question for a person with print emulation open, and
 * `Receipt.tsx`'s stylesheet is the thing that answers it.
 */

/** Still Water 500ml, €1.20 tax-inclusive at the standard 23%. */
const WATER = '5099999000011'

/** White Sliced Pan, €2.10 and zero-rated — so the breakdown has two rows. */
const BREAD = '5099999000035'

async function scan(page: Page, code: string): Promise<void> {
  await page.keyboard.type(code, { delay: 0 })
  await page.keyboard.press('Enter')
}

async function ensureDrawerOpen(page: Page): Promise<void> {
  const float = page.getByLabel(/Opening float/)

  await expect(float.or(page.getByTestId('cart-total'))).toBeVisible()

  if (await float.isVisible()) {
    await float.fill('100.00')
    await page.getByRole('button', { name: 'Open the drawer' }).click()
    // The banner, not the toast that announces it. That toast is a success tone and
    // dismisses itself after four seconds, so asserting on it makes this helper a
    // race. It stayed invisible for months because an accumulated `pos_e2e` always
    // had the drawer open already, so this branch never ran — it surfaced the first
    // time the suite met a fresh database.
    await expect(page.getByRole('banner')).toContainText('Drawer open')
  }
}

/** Rings the two items through and pays, leaving the completion panel on screen. */
async function sellWaterAndBread(page: Page): Promise<void> {
  await scan(page, WATER)
  await page.waitForTimeout(400)
  await scan(page, BREAD)

  await expect(page.getByTestId('cart-line')).toHaveCount(2)
  await expect(page.getByTestId('cart-total')).toHaveText('€3.30')

  await page.getByTestId('take-cash').click()
  await page.getByRole('button', { name: '€5.00' }).click()
  await page.getByTestId('complete-sale').click()

  await expect(page.getByTestId('sale-complete')).toBeVisible()
}

test.describe('receipt', () => {
  test.beforeEach(async ({ page }) => {
    await enrolDevice(page)
    await signIn(page, 'cashier')
    await page.goto('/register')
    await ensureDrawerOpen(page)
  })

  test('a completed sale prints a receipt that matches what was rung', async ({ page }) => {
    await sellWaterAndBread(page)

    await page.getByRole('button', { name: 'Print receipt' }).click()

    const receipt = page.getByTestId('receipt')
    await expect(receipt).toBeVisible()

    // The lines, as they were rung — from the sale's snapshots, not the catalog.
    await expect(receipt).toContainText('Still Water 500ml')
    await expect(receipt).toContainText('White Sliced Pan')

    // The total the customer paid, and the change they were handed. Both are the
    // server's figures; the client has computed neither.
    await expect(receipt).toContainText('€3.30')
    await expect(receipt).toContainText('€1.70')

    // Two rates, because a single tax line would be wrong for one of the items.
    const breakdown = page.getByTestId('receipt-tax-breakdown')
    await expect(breakdown).toContainText('0%')
    await expect(breakdown).toContainText('23%')

    // The shop's own footer, configured on the tenant and rendered server-side.
    await expect(receipt).toContainText(RECEIPT_FOOTER)

    // An original. This copy is the one handed over at the counter.
    await expect(page.getByTestId('receipt-reprint')).toHaveCount(0)

    // In-page, and closable without a mouse — invariant 10. No browser dialog
    // was opened to get here, and none is needed to leave.
    await page.keyboard.press('Escape')
    await expect(page.getByTestId('receipt')).toHaveCount(0)
  })

  test('the tax lines on the paper add up to the tax the sale recorded', async ({ page }) => {
    // §6.1's exit criterion, checked through the whole stack rather than in the
    // builder's own unit test: a receipt whose VAT lines do not sum to its VAT
    // total is the one a tax authority asks about.
    await sellWaterAndBread(page)

    await page.getByRole('button', { name: 'Print receipt' }).click()
    await expect(page.getByTestId('receipt')).toBeVisible()

    const amounts = await page
      .getByTestId('receipt-tax-breakdown')
      .locator('tr')
      // The header row has no numbers in its last cell; the rate rows do.
      .evaluateAll((rows) =>
        rows
          .map((row) => row.querySelectorAll('td')[2]?.textContent ?? '')
          .filter((text) => /\d/.test(text))
          .map((text) => Math.round(Number(text.replace(/[^\d.-]/g, '')) * 100)),
      )

    // €1.20 inclusive at 23% is €0.22 of tax; the bread is zero-rated.
    expect(amounts).toEqual([0, 22])
  })

  test('the paper can be switched to A4 for a shop with no thermal printer', async ({ page }) => {
    await sellWaterAndBread(page)

    await page.getByRole('button', { name: 'Print receipt' }).click()
    await expect(page.getByTestId('receipt')).toHaveAttribute('data-paper', '80mm')

    await page.getByRole('button', { name: '80mm roll' }).click()

    await expect(page.getByTestId('receipt')).toHaveAttribute('data-paper', 'a4')
    await expect(page.getByTestId('receipt')).toContainText('Still Water 500ml')
  })

  test('printing is offered but never happens on its own', async ({ page }) => {
    /*
     * The rule `printPaper` exists to keep. `window.print()` blocks the tab, so
     * a till that printed when a sale completed — or when a receipt query
     * resolved — would stall a queue with a scanner still typing behind it.
     *
     * Asserted by counting: the real function is replaced before the sale, and
     * must still not have been called once the completion panel is up.
     */
    await page.addInitScript(() => {
      const counter = { calls: 0 }
      ;(window as unknown as { __printCalls: typeof counter }).__printCalls = counter
      window.print = () => {
        counter.calls++
      }
    })

    await page.reload()
    await ensureDrawerOpen(page)
    await sellWaterAndBread(page)

    await page.getByRole('button', { name: 'Print receipt' }).click()
    await expect(page.getByTestId('receipt')).toBeVisible()

    // Opening the preview is not printing.
    expect(
      await page.evaluate(
        () => (window as unknown as { __printCalls: { calls: number } }).__printCalls.calls,
      ),
    ).toBe(0)

    // And the press is: this is the only thing in the app that may call it, and
    // the stub is what makes asserting so safe.
    await page.getByRole('button', { name: 'Print', exact: true }).click()

    expect(
      await page.evaluate(
        () => (window as unknown as { __printCalls: { calls: number } }).__printCalls.calls,
      ),
    ).toBe(1)
  })
})
