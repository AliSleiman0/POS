import { expect, test, type Page } from '@playwright/test'
import { enrolDevice, signIn } from './fixtures/actors'

/**
 * The reports screens, end to end.
 *
 * Phase 6.3. The arithmetic is asserted in the API suite against sales it made
 * itself; what is proved here is that an owner can actually reach the number —
 * §6.3's goal is a person seeing whether the drawer balanced, and an endpoint
 * nobody can open does not meet it.
 *
 * Serial, because these tests ring sales into a shared tenant and then assert on
 * a report over that tenant. A neighbour committing its own sale mid-read would
 * make the figures move underneath the assertion.
 */

const WATER = '5099999000011'
const BREAD = '5099999000035'

test.describe.configure({ mode: 'serial' })

async function scan(page: Page, code: string): Promise<void> {
  await page.keyboard.type(code, { delay: 0 })
  await page.keyboard.press('Enter')
}

/**
 * Hands the till to somebody else, the way a shop does.
 *
 * `signIn` alone is not enough once a session exists: `/login` sits outside the
 * auth guard and sends an already-authenticated visitor back to `/`, so the form
 * is never really used and the old identity survives — which looked exactly like
 * a bug in the app until the header was read. (The app itself is fine: adopting
 * a session refetches `/auth/me` before it returns, so a real swap does swap.)
 */
async function switchTo(page: Page, role: 'owner' | 'manager' | 'cashier'): Promise<void> {
  await page.goto('/')
  await page.getByRole('button', { name: 'Sign out' }).click()
  await page.waitForURL(/\/login/)

  await signIn(page, role)
}

/** Rings one sale through as the cashier, so the report has something to add up. */
async function sell(page: Page): Promise<void> {
  await enrolDevice(page)
  await signIn(page, 'cashier')
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
  await scan(page, BREAD)
  await expect(page.getByTestId('cart-total')).toHaveText('€3.30')

  await page.getByTestId('take-cash').click()
  await page.getByRole('button', { name: '€5.00' }).click()
  await page.getByTestId('complete-sale').click()

  await expect(page.getByTestId('sale-complete')).toBeVisible()
}

test.describe('reports', () => {
  test('an owner reads the day and sees what the drawer should hold', async ({ page }) => {
    await sell(page)

    // The reconciliation view belongs to whoever may close a drawer.
    await switchTo(page, 'owner')
    await page.getByRole('link', { name: 'Reports' }).click()

    await expect(page).toHaveURL(/\/reports\/daily/)

    // Two rates from one basket, so a single-rate report would be wrong for one
    // of the items.
    const breakdown = page.getByTestId('report-tax-by-rate')
    await expect(breakdown).toContainText('0%')
    await expect(breakdown).toContainText('23%')

    // The drawer is still open, so the figure is an expectation and says so —
    // nothing has been counted and no variance is claimed.
    await expect(page.getByTestId('report-provisional')).toBeVisible()
    await expect(page.getByTestId('report-cash')).toContainText('Not counted')
    await expect(page.getByTestId('report-variance')).toHaveCount(0)

    // And the expectation is a real number rather than a dash.
    await expect(page.getByTestId('report-expected-cash')).toContainText('€')
  })

  test('a cashier is not shown the reconciliation and cannot reach it', async ({ page }) => {
    await enrolDevice(page)
    await signIn(page, 'cashier')

    // Not offered.
    await expect(page.getByRole('link', { name: 'Reports' })).toHaveCount(0)

    // And not reachable by typing the URL either — the gate is a route guard,
    // and the server refuses the call regardless (invariant 7).
    await page.goto('/reports/daily')

    await expect(page.getByTestId('report-cash')).toHaveCount(0)
  })

  test('the owner sees margins and the manager does not', async ({ page }) => {
    await sell(page)

    await switchTo(page, 'owner')
    await page.goto('/reports/daily')

    // Cost prices are the owner's commercial position.
    const margins = page.getByTestId('report-margins')
    await expect(margins).toBeVisible()

    // Still Water has a cost, so it has a margin.
    await expect(margins).toContainText('Still Water 500ml')

    /*
     * The bread does not, and the report says "Unknown" rather than treating a
     * missing cost as free. The total is then unknown too — one product with no
     * cost makes the whole figure a guess, and the guess would always be in the
     * flattering direction.
     */
    await expect(margins).toContainText('Unknown')
    await expect(page.getByTestId('margin-total')).toHaveText('—')

    await switchTo(page, 'manager')
    await page.goto('/reports/daily')

    // The manager still reconciles drawers — they hold CanCloseShift — so the
    // report is there and only the margin panel is absent.
    await expect(page.getByTestId('report-cash')).toBeVisible()
    await expect(page.getByTestId('report-margins')).toHaveCount(0)
  })

  test('a day with no trading is zeroes rather than an error', async ({ page }) => {
    await signIn(page, 'owner')
    await page.goto('/reports/daily')

    await page.getByLabel('Trading day').fill('2020-01-01')

    // A shop that opened and sold nothing still has to cash up against its
    // float, so this has to render.
    await expect(page.getByTestId('report-total')).toHaveText('€0.00')
    await expect(page.getByText('No sales in this period.')).toBeVisible()
    await expect(page.getByText('No sales were voided.')).toBeVisible()
  })
})
