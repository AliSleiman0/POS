import { expect, test, type Page } from '@playwright/test'
import { enrolDevice, signIn } from './fixtures/actors'
import { MANAGER_NAME, MANAGER_PIN, OWNER_EMAIL, PASSWORD, TENANT_SLUG } from './fixtures/seed'

/**
 * The register, end to end against a real API and a real Postgres.
 *
 * Phases 5.1 to 5.4: the drawer, the cart, the scanner, the adjustments and the
 * money. The receipt has its own spec — `receipt.spec.ts`, Phase 6.2.
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

  /**
   * How many sales this shop has, straight from the API.
   *
   * **Paged to the end, not one request.** This read one page of 100 until 6.2,
   * which was fine while `pos_e2e` held fewer than that — and `pos_e2e` is
   * seeded but never dropped, so every run adds to it. The moment the hundredth
   * sale landed the count saturated, and "one sale was written" started failing
   * as `expected 101, received 100`: a test that had quietly stopped being able
   * to observe the thing it asserts. `limit` is capped at 200 server-side, so
   * raising the number only moves the cliff.
   */
  async function saleCount(page: Page): Promise<number> {
    const token = await ownerToken(page)

    let cursor: string | null = null
    let total = 0

    do {
      const url =
        '/api/v1/sales?limit=200' + (cursor === null ? '' : `&cursor=${encodeURIComponent(cursor)}`)

      const response = await page.request.get(url, {
        headers: { Authorization: `Bearer ${token}` },
      })

      const page_: { items: unknown[]; nextCursor: string | null; hasMore: boolean } =
        await response.json()

      total += page_.items.length
      cursor = page_.hasMore ? page_.nextCursor : null
    } while (cursor !== null)

    return total
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

  test('a reload keeps the basket', async ({ page }) => {
    // The cheap half of persistence, and the one a cashier meets weekly. Twenty
    // items into a shop rather than one, this is a minute of a queue's time.
    await scan(page, WATER)
    await scan(page, CHEDDAR)
    await expect(page.getByTestId('cart-line')).toHaveCount(2)

    await page.reload()

    await expect(page.getByTestId('cart-line')).toHaveCount(2)
    await expect(page.getByTestId('cart-total')).toHaveText('€14.15')
  })

  test('a reload after the payment landed shows the sale, and charges once', async ({ page }) => {
    /*
     * The milestone.
     *
     * The request reaches the server and commits; the answer is thrown away and
     * the page is reloaded before anyone can retry. The till comes back holding
     * nothing but the GUID it sent — and asks what that GUID bought, rather
     * than re-submitting to find out. Re-submitting would be safe *here*, where
     * the sale landed, and would take the money in the case below where it did
     * not.
     *
     * Falsify by removing the `writeSaleInFlight` call in `RegisterPage`: the
     * reload then comes back to an ordinary empty till, the cashier concludes
     * the payment failed, and rings the basket again as a second sale.
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

      swallowed = true
      await route.fetch()
      await route.abort('connectionaborted')
    })

    await page.getByTestId('complete-sale').click()
    await expect(page.getByText(/Could not complete the sale/)).toBeVisible()

    await page.reload()

    // Found, and said plainly: this was paid for, you did not see it happen.
    await expect(page.getByTestId('sale-complete')).toBeVisible()
    await expect(page.getByTestId('sale-recovered')).toBeVisible()
    await expect(page.getByTestId('change-due')).toHaveText('€0.00')

    // The cart is cleared, because that sale is over.
    await expect(page.getByTestId('cart-line')).toHaveCount(0)

    expect(await saleCount(page)).toBe(before + 1)
  })

  test('a reload after the payment never arrived restores the tender pad', async ({ page }) => {
    /*
     * The other answer, and the reason this is a read rather than a re-POST.
     *
     * The request never reaches the server, so nothing was charged. A till that
     * assumed the worst and re-submitted on load would take money for a sale
     * nobody had finished — on a page load, with the customer possibly gone.
     */
    const before = await saleCount(page)

    await scan(page, WATER)
    await takeCash(page)
    await page.getByRole('button', { name: '€5.00' }).click()

    let blocked = false

    await page.route('**/api/v1/sales', async (route) => {
      if (route.request().method() !== 'POST' || blocked) {
        await route.continue()
        return
      }

      // Killed before the server sees it, unlike the test above.
      blocked = true
      await route.abort('connectionrefused')
    })

    await page.getByTestId('complete-sale').click()
    await expect(page.getByText(/Could not complete the sale/)).toBeVisible()

    await page.reload()

    // Back on the tender pad with the same amount counted, so finishing is one
    // press rather than a rebuild.
    await expect(page.getByTestId('tender-panel')).toBeVisible()
    await expect(page.getByTestId('tender-line')).toHaveCount(1)
    await expect(page.getByText(/That payment was not taken/)).toBeVisible()

    // Nothing was charged while the till worked that out.
    expect(await saleCount(page)).toBe(before)

    await page.getByTestId('complete-sale').click()

    await expect(page.getByTestId('sale-complete')).toBeVisible()
    await expect(page.getByTestId('change-due')).toHaveText('€3.80')

    // And exactly one sale, from the key that survived the reload.
    expect(await saleCount(page)).toBe(before + 1)
  })

  test('editing the basket after a lost response says what was already paid for', async ({
    page,
  }) => {
    /*
     * The dead end 5.4 left, which nothing had ever walked into.
     *
     * The server fingerprints the request body, so the same key with a
     * *different* basket is `idempotency-key-reused` rather than a replay. A
     * cashier reaches it by retrying a submit whose response was lost and then
     * changing the cart — and until this branch existed the till said "try
     * again", advice that would have failed identically for ever.
     *
     * What it must do instead: find out what the key bought, say so, and let
     * the basket on the screen be sold as its own sale.
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

      swallowed = true
      await route.fetch()
      await route.abort('connectionaborted')
    })

    await page.getByTestId('complete-sale').click()
    await expect(page.getByText(/Could not complete the sale/)).toBeVisible()

    // The cashier backs out and adds an item — the customer changed their mind
    // while the till was failing. The key in hand is now for a basket that no
    // longer exists.
    await page.keyboard.press('Escape')
    await scan(page, CHEDDAR)
    await expect(page.getByTestId('cart-line')).toHaveCount(2)

    await takeCash(page)
    await page.getByRole('button', { name: 'Exact' }).click()
    await page.getByTestId('complete-sale').click()

    // Not "try again": the sale that key bought, named, with the warning that
    // money has already changed hands.
    await expect(page.getByTestId('sale-recovered')).toBeVisible()
    await expect(page.getByText(/was already paid for/)).toBeVisible()

    // One sale so far — the first one. The 409 wrote nothing.
    expect(await saleCount(page)).toBe(before + 1)

    /*
     * The tender pad is deliberately dismissed, and the counted amounts with it.
     *
     * This is not a "press again" — money has already changed hands for a
     * different basket, and the cashier has to settle that with the customer
     * before taking more. Making them start the tender again is the point:
     * one more press is cheap, and a Complete button still sitting under their
     * thumb is an invitation to charge twice.
     */
    await expect(page.getByTestId('tender-panel')).toHaveCount(0)

    // But the basket is not stuck. It has an identity of its own now, so when
    // the cashier does decide to sell it, it goes through.
    await takeCash(page)
    await page.getByRole('button', { name: 'Exact' }).click()
    await page.getByTestId('complete-sale').click()

    await expect(page.getByTestId('sale-complete')).toBeVisible()
    expect(await saleCount(page)).toBe(before + 2)
  })
})
