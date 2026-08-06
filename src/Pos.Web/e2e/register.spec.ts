import { expect, test, type Page } from '@playwright/test'
import { enrolDevice, signIn } from './fixtures/actors'
import { MANAGER_NAME, MANAGER_PIN, OWNER_EMAIL, PASSWORD, TENANT_SLUG } from './fixtures/seed'

/**
 * The register, end to end against a real API and a real Postgres.
 *
 * Phases 5.1 to 5.4: the drawer, the cart, the scanner, the adjustments and the
 * money. The receipt is 6.1 and is deliberately not asserted here.
 *
 * The barcodes are the seeded catalog's own (`tools/Pos.Seed/CatalogSeeder.cs`):
 * `5099999000011` is Still Water 500ml at €1.20, tax-inclusive.
 */

/** Still Water 500ml, seeded with two codes because a multipack scans differently. */
const WATER = '5099999000011'
const WATER_NAME = 'Still Water 500ml'

/** Irish Cheddar, sold by the kilogram at €12.95. */
const CHEDDAR = '2000000000015'

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

/**
 * 5.4 — taking the money.
 *
 * The milestone where a cart becomes a sale. `POST /sales` has been built and
 * tested from the .NET side since Phase 3; nothing on the till had ever called
 * it until now.
 *
 * The test that matters most is the double-submit one, and it is deliberately
 * *not* a double click: the button disables itself while a submit is in flight,
 * so a double click proves the courtesy works and says nothing about the
 * mechanism. What is exercised instead is the failure the mechanism exists for —
 * a request that reaches the server and whose response is lost.
 */
test.describe('cash payment', () => {
  /*
   * Serial, unlike everything else in this file.
   *
   * `fullyParallel` is on, so sibling tests otherwise run at once — and these are
   * the only tests in the suite that *write sales into a shared tenant*. Counting
   * rows is the only honest way to assert "exactly one sale", and a count is
   * meaningless while a neighbour is committing its own. Everything above this
   * block reads or builds a cart and can stay parallel.
   */
  test.describe.configure({ mode: 'serial' })

  test.beforeEach(async ({ page }) => {
    await enrolDevice(page)
    await signIn(page, 'cashier')
    await page.goto('/register')
    await ensureDrawerOpen(page)
  })

  /** An owner's token, for reading back what the till wrote. */
  async function ownerToken(page: Page): Promise<string> {
    const login = await page.request.post('/api/v1/auth/login', {
      data: { tenantSlug: TENANT_SLUG, email: OWNER_EMAIL, password: PASSWORD },
    })

    return ((await login.json()) as { accessToken: string }).accessToken
  }

  /** How many sales this shop has, straight from the API. */
  async function saleCount(page: Page): Promise<number> {
    const sales = await page.request.get('/api/v1/sales?limit=100', {
      headers: { Authorization: `Bearer ${await ownerToken(page)}` },
    })

    return ((await sales.json()) as { items: unknown[] }).items.length
  }

  async function takeCash(page: Page): Promise<void> {
    await page.getByTestId('take-cash').click()
    await expect(page.getByTestId('tender-panel')).toBeVisible()
  }

  test('exact cash completes the sale and leaves a clean cart', async ({ page }) => {
    await scan(page, WATER)
    await expect(page.getByTestId('cart-total')).toHaveText('€1.20')

    await takeCash(page)

    // "Exact" is the first quick-cash button and the commonest press.
    await page.getByRole('button', { name: 'Exact' }).click()
    await expect(page.getByTestId('tender-remaining')).toHaveText('€0.00')

    await page.getByTestId('complete-sale').click()

    await expect(page.getByTestId('sale-complete')).toBeVisible()
    await expect(page.getByTestId('change-due')).toHaveText('€0.00')

    // Ready for the next customer with no extra click — the phase doc's bar.
    await expect(page.getByTestId('cart-line')).toHaveCount(0)
    await expect(page.getByText('Scan an item to start.')).toBeVisible()

    /*
     * And the total is zero, not the last customer's.
     *
     * A bug since 5.1 that a completed sale makes routine: `useQuote` keeps the
     * previous answer on screen so a scan does not blank the total, and an
     * emptied cart inherited it — so "€1.20" sat under the change due for a cart
     * containing nothing. Before 5.4 it took a cart void to see it.
     */
    await expect(page.getByTestId('cart-total')).toHaveText('€0.00')

    // And the next scan is what clears the change off the screen.
    await scan(page, WATER)
    await expect(page.getByTestId('sale-complete')).toHaveCount(0)
  })

  test('change due comes back from the server, not from the client', async ({ page }) => {
    await scan(page, WATER)
    await takeCash(page)

    await page.getByRole('button', { name: '€5.00' }).click()
    await page.getByTestId('complete-sale').click()

    await expect(page.getByTestId('change-due')).toHaveText('€3.80')
  })

  test('a split tender runs the balance down to zero and writes one sale', async ({ page }) => {
    const before = await saleCount(page)

    await scan(page, WATER)
    await scan(page, CHEDDAR)
    await expect(page.getByTestId('cart-total')).toHaveText('€14.15')

    await takeCash(page)

    // Two amounts typed on the keypad, which is what a cashier does with a
    // handful of notes and coins.
    await page.keyboard.type('10', { delay: 40 })
    await page.keyboard.press('Enter')
    await expect(page.getByTestId('tender-remaining')).toHaveText('€4.15')

    await page.keyboard.type('4.15', { delay: 40 })
    await page.keyboard.press('Enter')
    await expect(page.getByTestId('tender-remaining')).toHaveText('€0.00')
    await expect(page.getByTestId('tender-line')).toHaveCount(2)

    await page.getByTestId('complete-sale').click()
    await expect(page.getByTestId('change-due')).toHaveText('€0.00')

    expect(await saleCount(page)).toBe(before + 1)
  })

  test('a lost response does not charge the customer twice', async ({ page }) => {
    /*
     * The mechanism, not the courtesy.
     *
     * The first request reaches the server and commits; its response is thrown
     * away, so the till sees a network failure and the cashier presses again.
     * The second attempt carries the *same* idempotency key — minted when
     * tendering began — so the server returns the original sale.
     *
     * Falsify by minting the key inside `useCompleteSale` instead of in the
     * cart: the retry then carries a key the server has never seen, and this
     * shop takes the money twice.
     */
    const before = await saleCount(page)

    await scan(page, WATER)
    await takeCash(page)
    await page.getByRole('button', { name: 'Exact' }).click()

    let swallowed = false

    await page.route('**/api/v1/sales', async (route) => {
      if (route.request().method() !== 'POST' || swallowed) {
        await route.continue()
        return
      }

      // Let it land, then drop the answer on the floor.
      swallowed = true
      await route.fetch()
      await route.abort('connectionaborted')
    })

    await page.getByTestId('complete-sale').click()

    // The till says so, and keeps the cart: nothing to rebuild, and the same
    // key is still in hand.
    await expect(page.getByText(/Could not complete the sale/)).toBeVisible()
    await expect(page.getByTestId('cart-line')).toHaveCount(1)

    await page.getByTestId('complete-sale').click()

    await expect(page.getByTestId('sale-complete')).toBeVisible()

    // The visible half of invariant 6.
    await expect(page.getByTestId('sale-replayed')).toBeVisible()

    // The half that matters.
    expect(await saleCount(page)).toBe(before + 1)
  })

  test('a sale decrements the stock it sold', async ({ page }) => {
    /** What the ledger says is on the shelf. */
    async function onHand(): Promise<number> {
      const authorization = { Authorization: `Bearer ${await ownerToken(page)}` }

      const product = await page.request.get(`/api/v1/products/by-barcode/${WATER}`, {
        headers: authorization,
      })

      const { productId } = (await product.json()) as { productId: string }

      const stock = await page.request.get(`/api/v1/stock?limit=100`, { headers: authorization })

      const body = (await stock.json()) as {
        items: { productId: string; onHand: number | string }[]
      }

      return Number(body.items.find((item) => item.productId === productId)?.onHand)
    }

    // Before and after, rather than "the newest movement is a Sale": the
    // movement list is oldest-first and the seeded Receive sits at the top of
    // it, so an index-based assertion would be reading the wrong row and
    // passing or failing for reasons unrelated to this sale.
    const before = await onHand()

    await scan(page, WATER)
    await takeCash(page)
    await page.getByRole('button', { name: 'Exact' }).click()
    await page.getByTestId('complete-sale').click()

    await expect(page.getByTestId('sale-complete')).toBeVisible()

    // Read back through the API rather than a screen, so this asserts the ledger
    // moved rather than that a component re-rendered.
    expect(await onHand()).toBe(before - 1)
  })

  test('a discounted sale spends the manager approval', async ({ page }) => {
    // 5.3 built the grant and 5.4 is the first thing that can spend one. Until
    // now no test had taken that path from the UI at all.
    const before = await saleCount(page)

    await scan(page, WATER)

    await page
      .getByTestId('cart-line')
      .getByRole('button', { name: /^Select / })
      .click()
    await page.getByRole('button', { name: 'Discount', exact: true }).click()

    await page.getByRole('button', { name: MANAGER_NAME }).click()
    await page.getByLabel('Manager PIN').fill(MANAGER_PIN)
    await page.getByRole('button', { name: 'Approve' }).click()

    await page.getByTestId('adjust-amount').fill('0.20')
    await page.getByRole('button', { name: 'Apply' }).click()

    await expect(page.getByTestId('cart-total')).toHaveText('€1.00')

    await takeCash(page)
    await page.getByRole('button', { name: 'Exact' }).click()
    await page.getByTestId('complete-sale').click()

    await expect(page.getByTestId('sale-complete')).toBeVisible()
    expect(await saleCount(page)).toBe(before + 1)
  })

  test('backing out of the tender step keeps the sale, not just the cart', async ({ page }) => {
    await scan(page, WATER)
    await takeCash(page)

    await page.keyboard.press('Escape')
    await expect(page.getByTestId('tender-panel')).toHaveCount(0)
    await expect(page.getByTestId('cart-line')).toHaveCount(1)

    // Same sale, tendered again — and it must still complete exactly once.
    const before = await saleCount(page)

    await takeCash(page)
    await page.getByRole('button', { name: 'Exact' }).click()
    await page.getByTestId('complete-sale').click()

    await expect(page.getByTestId('sale-complete')).toBeVisible()
    expect(await saleCount(page)).toBe(before + 1)
  })
})
