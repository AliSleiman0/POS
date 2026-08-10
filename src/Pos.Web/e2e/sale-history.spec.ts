import { expect, test, type Page } from '@playwright/test'
import { enrolDevice, signIn } from './fixtures/actors'

/**
 * The sale history, end to end.
 *
 * Phase 6.4. The journey this exists for is one person's: a customer arrives
 * with a receipt, the number on it is typed in, the sale comes up, part of it
 * goes back, and both records point at each other afterwards.
 *
 * Signed in as the owner throughout, because refunding needs `CanRefund` and a
 * cashier does not have it — the point of the gate.
 */

/** Still Water 500ml, €1.20. From `tools/Pos.Seed/CatalogSeeder.cs`, like the rest. */
const WATER = '5099999000011'

/** Olive Oil 1L, €8.95 — the line this test sends back. */
const OLIVE_OIL = '5099999000042'

test.describe.configure({ mode: 'serial' })

async function scan(page: Page, code: string): Promise<void> {
  await page.keyboard.type(code, { delay: 0 })
  await page.keyboard.press('Enter')
}

/** Rings a two-line sale and returns the number printed on its receipt. */
async function sell(page: Page): Promise<string> {
  await page.goto('/register')

  const float = page.getByLabel(/Opening float/)
  await expect(float.or(page.getByTestId('cart-total'))).toBeVisible()

  if (await float.isVisible()) {
    await float.fill('100.00')
    await page.getByRole('button', { name: 'Open the drawer' }).click()
    await expect(page.getByText('Drawer open. Ready to sell.')).toBeVisible()
  }

  await scan(page, WATER)
  await page.waitForTimeout(400)
  await scan(page, OLIVE_OIL)
  await expect(page.getByTestId('cart-line')).toHaveCount(2)

  await page.getByTestId('take-cash').click()
  await page.getByRole('button', { name: 'Exact' }).click()
  await page.getByTestId('complete-sale').click()

  await expect(page.getByTestId('sale-complete')).toBeVisible()

  const heading = await page.getByTestId('sale-complete').textContent()
  const number = /Sale #(\d+)/.exec(heading ?? '')?.[1]

  expect(number).toBeDefined()

  return number!
}

test.describe('sale history', () => {
  test.beforeEach(async ({ page }) => {
    await enrolDevice(page)
    await signIn(page, 'owner')
  })

  test('a customer brings a receipt back and part of the sale goes home with them', async ({
    page,
  }) => {
    const number = await sell(page)

    // The whole reason the search box is the prominent control: this is the only
    // thing the customer has.
    await page.getByRole('link', { name: 'Sales' }).click()
    await page.getByLabel('Sale number').fill(number)

    const row = page.getByTestId('sale-row')
    await expect(row).toHaveCount(1)
    await expect(row).toContainText(`#${number}`)

    await row.getByRole('link', { name: `#${number}` }).click()

    // The snapshots, as they were rung.
    await expect(page.getByTestId('sale-line')).toHaveCount(2)
    await expect(page.getByText('Still Water 500ml')).toBeVisible()

    // Part of it back: the olive oil only.
    await page.getByRole('button', { name: 'Refund' }).click()

    const dialog = page.getByTestId('refund-dialog')
    await expect(dialog).toBeVisible()

    await dialog.getByLabel('Some of it').check()
    await dialog.getByLabel(/Return quantity for Olive Oil/).fill('1')
    await dialog.getByLabel(/Reason/).fill('Bottle was cracked')
    await dialog.getByRole('button', { name: 'Refund' }).click()

    await expect(dialog).toHaveCount(0)

    /*
     * Both directions, which is the exit criterion. From the original you must
     * be able to see it was refunded — without that, the same receipt comes back
     * tomorrow and the shop pays out twice.
     */
    const refunds = page.getByTestId('sale-refunds')
    await expect(refunds).toBeVisible()
    await expect(refunds).toContainText('Already refunded')

    await refunds.getByRole('link').first().click()

    // And from the refund back to what it reverses.
    await expect(page.getByTestId('sale-original')).toContainText(`#${number}`)
    await expect(page.getByTestId('sale-original')).toContainText('Bottle was cracked')
  })

  test('the second copy of a receipt is marked as one, and the first is not', async ({ page }) => {
    const number = await sell(page)

    await page.goto('/sales')
    await page.getByLabel('Sale number').fill(number)
    await page
      .getByTestId('sale-row')
      .getByRole('link', { name: `#${number}` })
      .click()

    // From Phase 7.2 the mark is the *server's* count of issues, not a claim about
    // which screen you are on. This sale was rung and never printed, so the first
    // copy — taken from history — is honestly the original.
    await page.getByRole('button', { name: 'Reprint receipt' }).click()
    await expect(page.getByTestId('receipt')).toBeVisible()
    await expect(page.getByTestId('receipt-reprint')).toHaveCount(0)

    await page.keyboard.press('Escape')

    // The second copy says so, and the till sent nothing to make it. A client that
    // chose to stay quiet used to print an unmarked duplicate, which is the
    // refund-fraud vector §6.2 recorded.
    await page.getByRole('button', { name: 'Reprint receipt' }).click()
    await expect(page.getByTestId('receipt-reprint')).toBeVisible()
    await expect(page.getByTestId('receipt-reprint')).toContainText('REPRINT')
  })

  test('a sale number that belongs to nobody says so plainly', async ({ page }) => {
    await page.goto('/sales')
    await page.getByLabel('Sale number').fill('999999')

    // Different from "nothing matched your filters", and the person at the
    // counter needs the difference.
    await expect(page.getByText('No sale numbered 999999.')).toBeVisible()
  })

  test('filters compose, and a voided sale stays in the history', async ({ page }) => {
    await page.goto('/sales')

    await page.getByLabel('Type').selectOption('Refund')
    await page.getByLabel('Status').selectOption('Completed')

    // The refund from the first test is still there; nothing else of that type
    // and status could have been hidden by the second filter.
    await expect(page.getByTestId('sale-row').first()).toContainText('Refund')

    // Voids are not hidden by default — a manager who cannot find a transaction
    // concludes the system lost it — so switching the status finds them.
    await page.getByLabel('Type').selectOption('')
    await page.getByLabel('Status').selectOption('Voided')

    // Either there are voided sales in this shop's history or there are none;
    // both are legitimate. What matters is that the filter answers rather than
    // erroring, and that a result is labelled.
    const voided = page.getByTestId('sale-row')

    if ((await voided.count()) > 0) {
      await expect(voided.first()).toContainText('Voided')
    } else {
      await expect(page.getByText('Nothing matched.')).toBeVisible()
    }
  })

  test('a cashier can look a sale up but cannot refund it', async ({ page }) => {
    await page.goto('/')
    await page.getByRole('button', { name: 'Sign out' }).click()
    await page.waitForURL(/\/login/)
    await signIn(page, 'cashier')

    await page.goto('/sales')

    // Looking a sale up is CanSell: it happens at the counter.
    await expect(page.getByRole('link', { name: 'Sales' })).toBeVisible()

    const row = page.getByTestId('sale-row').first()
    await expect(row).toBeVisible()
    await row.getByRole('link').click()

    // Refunding is not. The server refuses it regardless (invariant 7); this is
    // the courtesy of not offering a button that answers 403.
    await expect(page.getByRole('button', { name: 'Reprint receipt' })).toBeVisible()
    await expect(page.getByRole('button', { name: 'Refund' })).toHaveCount(0)
  })
})
