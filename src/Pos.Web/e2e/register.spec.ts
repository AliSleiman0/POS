import { expect, test, type Page } from '@playwright/test'
import { enrolDevice, signIn } from './fixtures/actors'
import { MANAGER_NAME, MANAGER_PIN } from './fixtures/seed'

/**
 * The register, end to end against a real API and a real Postgres.
 *
 * Phases 5.1 and 5.2: the drawer, the cart, and the scanner. Tender, receipt and
 * the stock decrement are 5.4/5.6 and are deliberately not asserted here.
 *
 * The barcodes are the seeded catalog's own (`tools/Pos.Seed/CatalogSeeder.cs`):
 * `5099999000011` is Still Water 500ml at €1.20, tax-inclusive.
 */

/** Still Water 500ml, seeded with two codes because a multipack scans differently. */
const WATER = '5099999000011'
const WATER_NAME = 'Still Water 500ml'

/**
 * A wedge scanner, as far as the browser can tell: characters with no delay,
 * then Enter. This is the whole mechanism under test — the app has nothing to go
 * on but the timing.
 */
async function scan(page: Page, code: string): Promise<void> {
  await page.keyboard.type(code, { delay: 0 })
  await page.keyboard.press('Enter')
}

/**
 * A burst at the speed hardware actually produces, dispatched in one JS turn.
 *
 * `page.keyboard` cannot express this. Every CDP keystroke is a round trip that
 * waits on the renderer's main thread, so a burst arriving *while React is
 * rendering the previous one* is delivered over hundreds of milliseconds —
 * which is not what a device does. Real hardware hands the whole code to the OS
 * in a few milliseconds, and Chromium timestamps the events when they were
 * generated rather than when a busy renderer got to them.
 *
 * Two tests need that fidelity: the double-fire (`times: 2`), and the
 * stand-down rule, where the burst has to land on the focused field at machine
 * speed or the test passes for the wrong reason. Everything else in this spec
 * uses the real keyboard, which is what proves the listener is wired to real
 * input at all.
 */
async function machineBurst(
  page: Page,
  code: string,
  { times = 1, toFocused = false }: { times?: number; toFocused?: boolean } = {},
): Promise<void> {
  await page.evaluate(
    ({ value, repeats, focused }) => {
      const target = focused ? (document.activeElement ?? document) : document

      for (let fire = 0; fire < repeats; fire++) {
        for (const character of value) {
          target.dispatchEvent(new KeyboardEvent('keydown', { key: character, bubbles: true }))
        }
        target.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }))
      }
    },
    { value: code, repeats: times, focused: toFocused },
  )
}

/** Opens the drawer if it is not already open. */
async function ensureDrawerOpen(page: Page): Promise<void> {
  const float = page.getByLabel(/Opening float/)

  // Wait for the drawer question to be *answered* before asking it: until
  // `GET /shifts/current` settles, neither panel is on screen and `isVisible()`
  // — which does not wait — would report "no float field" for a till that is
  // simply still asking.
  await expect(float.or(page.getByTestId('cart-total'))).toBeVisible()

  // `pos_e2e` is seeded but not dropped between runs, and there is no close-shift
  // UI until 6.3 — so a second run finds the drawer this first run opened. The
  // end state is what matters and is asserted either way.
  if (await float.isVisible()) {
    await float.fill('100.00')
    await page.getByRole('button', { name: 'Open the drawer' }).click()
    await expect(page.getByText('Drawer open. Ready to sell.')).toBeVisible()
  }

  await expect(page.getByRole('banner')).toContainText('Drawer open')
}

test.describe('register', () => {
  test.beforeEach(async ({ page }) => {
    await enrolDevice(page)
    await signIn(page, 'cashier')
    await page.goto('/register')
  })

  test('a cashier opens the drawer, scans an item and sees the server price it', async ({
    page,
  }) => {
    await ensureDrawerOpen(page)

    await scan(page, WATER)

    const line = page.getByTestId('cart-line')
    await expect(line).toHaveCount(1)
    await expect(line).toContainText(WATER_NAME)

    // The number the customer reads. It comes from POST /sales/quote — the
    // client never computes a total (CLAUDE.md invariant 3), and €1.20 is the
    // seeded tax-inclusive price.
    await expect(page.getByTestId('cart-total')).toHaveText('€1.20')

    // A second, deliberate scan of the same item: two units of one line, not
    // two lines, and the server re-prices it.
    await page.waitForTimeout(400)
    await scan(page, WATER)

    await expect(page.getByTestId('cart-line')).toHaveCount(1)
    await expect(page.getByTestId('cart-total')).toHaveText('€2.40')
  })

  test('a scanner firing twice for one item sells one', async ({ page }) => {
    await ensureDrawerOpen(page)

    // Charging twice for one item is the bug the debounce exists for.
    await machineBurst(page, WATER, { times: 2 })

    await expect(page.getByTestId('cart-line')).toHaveCount(1)
    await expect(page.getByTestId('cart-total')).toHaveText('€1.20')
    await expect(page.getByText('Same code again — counted once.')).toBeVisible()
  })

  test('typing into the product search does not put anything in the cart', async ({ page }) => {
    await ensureDrawerOpen(page)

    const search = page.getByLabel('Search products')

    // First, the ordinary thing: a cashier types and the search searches.
    await search.click()
    await search.type(WATER, { delay: 0 })
    await page.keyboard.press('Enter')

    await expect(page.getByText('Nothing matched.')).toBeVisible()
    await expect(page.getByTestId('cart-line')).toHaveCount(0)

    // Then the stand-down rule itself, at hardware speed and aimed at the
    // focused field — the failure mode being a cashier's search text
    // disappearing into a barcode buffer. Typing alone does not pin this: React
    // re-rendering between keystrokes slows a CDP burst enough that it fails
    // the pacing test anyway, so the assertion would pass even with the rule
    // removed. Verified by removing it.
    await search.fill('')
    await search.focus()
    await machineBurst(page, WATER, { toFocused: true })

    await expect(page.getByTestId('cart-line')).toHaveCount(0)
  })

  test('an unknown code is a banner, not a blocked screen', async ({ page }) => {
    await ensureDrawerOpen(page)

    await scan(page, '9999999999999')

    // Non-blocking by construction: no dialog, no alert(), and the till is
    // still usable behind it (CLAUDE.md invariant 10).
    await expect(page.getByTestId('unknown-code')).toContainText('9999999999999')
    await expect(page.getByRole('dialog')).toHaveCount(0)
    await expect(page.getByTestId('cart-line')).toHaveCount(0)

    // And it can be got rid of without a mouse-only flow.
    await page.getByRole('button', { name: 'Dismiss' }).click()
    await expect(page.getByTestId('unknown-code')).toHaveCount(0)
  })

  test('the whole cart is operable from the keyboard', async ({ page }) => {
    await ensureDrawerOpen(page)

    await scan(page, WATER)
    await expect(page.getByTestId('cart-line')).toHaveCount(1)

    // Selection, then a quantity on the keypad. Experienced staff never touch
    // the screen for this, and a mouse-only flow fails at a real till.
    await page.keyboard.press('ArrowDown')
    await page.keyboard.type('3', { delay: 0 })
    await page.keyboard.press('Enter')

    await expect(page.getByTestId('cart-line')).toContainText('3 ×')
    await expect(page.getByTestId('cart-total')).toHaveText('€3.60')

    // `+` steps it, and a barcode cannot contain one, so it is safe to reserve.
    await page.keyboard.press('+')
    await expect(page.getByTestId('cart-total')).toHaveText('€4.80')

    // Delete voids the selected line. Backspace does not — that edits the
    // quantity being typed.
    await page.keyboard.press('Delete')
    await expect(page.getByTestId('cart-line')).toHaveCount(0)
  })

  test('a cart void asks first, in the page', async ({ page }) => {
    await ensureDrawerOpen(page)

    await scan(page, WATER)
    await expect(page.getByTestId('cart-line')).toHaveCount(1)

    // Two clicks with a visible state change, never `window.confirm` — which
    // blocks the event loop and queues a scanner's keystrokes behind it.
    await page.getByRole('button', { name: 'Void cart' }).click()
    await page.getByRole('button', { name: 'Void the whole cart?' }).click()

    await expect(page.getByTestId('cart-line')).toHaveCount(0)
    await expect(page.getByText('Scan an item to start.')).toBeVisible()
  })

  test('the cart survives a trip to another screen', async ({ page }) => {
    await ensureDrawerOpen(page)

    await scan(page, WATER)
    await expect(page.getByTestId('cart-line')).toHaveCount(1)

    // A cashier checking a price mid-sale. The cart lives above the router's
    // outlet precisely so this does not throw it away.
    await page.getByRole('link', { name: 'Overview' }).click()
    await expect(page.getByRole('link', { name: 'Open the register' })).toBeVisible()
    await page.getByRole('navigation').getByRole('link', { name: 'Register' }).click()

    await expect(page.getByTestId('cart-line')).toHaveCount(1)
    await expect(page.getByTestId('cart-total')).toHaveText('€1.20')
  })
})

/**
 * 5.3 — discounts, price overrides, and a manager authorising one action.
 *
 * The behaviour that makes the flow worth having: a cashier can get a discount
 * approved **without the session changing hands**. They stay signed in, the sale
 * stays theirs, and `SaleLine.OverriddenBy` records the manager — asserted
 * server-side in `SaleOverrideTests`, because 5.4 is what actually posts a sale.
 *
 * What is asserted here is the till's half: the control a cashier cannot use
 * alone, the PIN step it goes through instead, and a total that came back from
 * the server rather than being subtracted on the client.
 */
test.describe('cart adjustments', () => {
  test.beforeEach(async ({ page }) => {
    await enrolDevice(page)
  })

  /** Selects the only line and opens the discount entry. */
  async function openDiscount(page: Page): Promise<void> {
    await page
      .getByTestId('cart-line')
      .getByRole('button', { name: /^Select / })
      .click()
    await page.getByRole('button', { name: 'Discount', exact: true }).click()
  }

  async function applyAmount(page: Page, amount: string): Promise<void> {
    await page.getByTestId('adjust-amount').fill(amount)
    await page.getByRole('button', { name: 'Apply' }).click()
  }

  test('a manager discounts a line and the server prices the result', async ({ page }) => {
    await signIn(page, 'manager')
    await page.goto('/register')
    await ensureDrawerOpen(page)

    await scan(page, WATER)
    await expect(page.getByTestId('cart-total')).toHaveText('€1.20')

    // Holding CanApplyDiscount, they are not asked to approve themselves.
    await openDiscount(page)
    await expect(page.getByTestId('manager-override')).toHaveCount(0)

    await applyAmount(page, '0.20')

    // €1.00 came back from POST /sales/quote. The client subtracted nothing —
    // it sent `discountAmount: 0.2` and displayed the answer (invariant 3).
    await expect(page.getByTestId('cart-total')).toHaveText('€1.00')
    await expect(page.getByTestId('line-discount')).toContainText('€0.20')
  })

  test('a cashier cannot reach the amount without a manager, and gets there with one', async ({
    page,
  }) => {
    await signIn(page, 'cashier')
    await page.goto('/register')
    await ensureDrawerOpen(page)

    await scan(page, WATER)
    await openDiscount(page)

    // The PIN step, not the amount field. This is the exit criterion as built:
    // a Cashier has no control that discounts on their own authority.
    await expect(page.getByTestId('manager-override')).toBeVisible()
    await expect(page.getByTestId('adjust-amount')).toHaveCount(0)

    await page.getByRole('button', { name: MANAGER_NAME }).click()
    await page.getByLabel('Manager PIN').fill(MANAGER_PIN)
    await page.getByRole('button', { name: 'Approve' }).click()

    // Now the amount, and the screen says whose authority it is on.
    await expect(page.getByTestId('adjust-amount')).toBeVisible()
    await expect(page.getByTestId('authorized-by')).toContainText(MANAGER_NAME)

    await applyAmount(page, '0.20')

    await expect(page.getByTestId('cart-total')).toHaveText('€1.00')
  })

  test('a cashier who cannot find a manager changes nothing', async ({ page }) => {
    await signIn(page, 'cashier')
    await page.goto('/register')
    await ensureDrawerOpen(page)

    await scan(page, WATER)
    await openDiscount(page)

    await page.getByRole('button', { name: 'Cancel' }).click()

    // Not a partially-applied discount, not an error state: the same cart.
    await expect(page.getByTestId('manager-override')).toHaveCount(0)
    await expect(page.getByTestId('adjust-amount')).toHaveCount(0)
    await expect(page.getByTestId('line-discount')).toHaveCount(0)
    await expect(page.getByTestId('cart-total')).toHaveText('€1.20')
  })

  test('a price override shows both prices, because the counter will ask', async ({ page }) => {
    await signIn(page, 'cashier')
    await page.goto('/register')
    await ensureDrawerOpen(page)

    await scan(page, WATER)

    await page
      .getByTestId('cart-line')
      .getByRole('button', { name: /^Select / })
      .click()
    await page.getByRole('button', { name: 'Change price' }).click()

    await page.getByRole('button', { name: MANAGER_NAME }).click()
    await page.getByLabel('Manager PIN').fill(MANAGER_PIN)
    await page.getByRole('button', { name: 'Approve' }).click()

    await applyAmount(page, '1.00')

    await expect(page.getByTestId('cart-total')).toHaveText('€1.00')

    // The old price struck through beside the new one: "why is this cheaper" is
    // asked at the counter, and the answer has to be on the screen being asked
    // about.
    await expect(page.getByTestId('price-override')).toContainText('€1.00')
    await expect(page.getByTestId('cart-line')).toContainText('€1.20')
  })

  test('a discount on the whole sale is spread by the server', async ({ page }) => {
    await signIn(page, 'manager')
    await page.goto('/register')
    await ensureDrawerOpen(page)

    await scan(page, WATER)
    await expect(page.getByTestId('cart-total')).toHaveText('€1.20')

    await page.getByRole('button', { name: 'Discount sale' }).click()
    await applyAmount(page, '0.20')

    await expect(page.getByTestId('cart-discount')).toContainText('€0.20')
    await expect(page.getByTestId('cart-total')).toHaveText('€1.00')
  })

  test('voiding the cart takes the manager authorisation with it', async ({ page }) => {
    // Otherwise a discount approved for one customer is available to whatever
    // the next person puts on the counter.
    await signIn(page, 'cashier')
    await page.goto('/register')
    await ensureDrawerOpen(page)

    await scan(page, WATER)
    await openDiscount(page)

    await page.getByRole('button', { name: MANAGER_NAME }).click()
    await page.getByLabel('Manager PIN').fill(MANAGER_PIN)
    await page.getByRole('button', { name: 'Approve' }).click()
    await applyAmount(page, '0.20')

    await expect(page.getByTestId('cart-total')).toHaveText('€1.00')

    await page.getByRole('button', { name: 'Void cart' }).click()
    await page.getByRole('button', { name: 'Void the whole cart?' }).click()

    // The next customer. The PIN is asked for again.
    await scan(page, WATER)
    await openDiscount(page)

    await expect(page.getByTestId('manager-override')).toBeVisible()
  })
})
