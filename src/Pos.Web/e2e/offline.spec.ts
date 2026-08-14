import { expect, test, type Page } from '@playwright/test'
import { enrolDevice, signIn } from './fixtures/actors'
import { OWNER_EMAIL, PASSWORD, TENANT_SLUG } from './fixtures/seed'

/**
 * Selling with the network off, against a real API and a real Postgres.
 *
 * **This is the spec that proves Phase 9 rather than describing it.** Everything
 * else is a unit: the pricing corpus proves two engines agree, the classifier
 * tests prove a refusal is read correctly, the mirror tests prove a page is
 * applied. None of them proves that a cashier can take money from a customer
 * while the shop's line is down and that the money arrives when it comes back.
 *
 * `context.setOffline(true)` is a blunt instrument and the phase doc says so —
 * it does not reproduce a flaky connection, which is where sync bugs actually
 * live. What it does reproduce exactly is the deterministic half, and that half
 * is worth having automated because it is the half that regresses silently.
 */

/** Still Water 500ml, €1.20 tax-inclusive. The seeded catalog's own code. */
const WATER = '5099999000011'
const WATER_NAME = 'Still Water 500ml'

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
  }

  await expect(page.getByRole('banner')).toContainText('Drawer open')
}

/**
 * Waits for the mirror to hold the catalog.
 *
 * The whole point of going offline afterwards is that the till already has what
 * it needs; a test that pulled the plug before the first sync would be checking
 * that an empty mirror fails, which it should and which proves nothing.
 */
async function waitForMirror(page: Page): Promise<void> {
  await expect(page.getByTestId('sync-status')).toContainText(/Prices (up to date|as of)/, {
    timeout: 20_000,
  })
}

async function ownerToken(page: Page): Promise<string> {
  const login = await page.request.post('/api/v1/auth/login', {
    data: { tenantSlug: TENANT_SLUG, email: OWNER_EMAIL, password: PASSWORD },
  })

  return ((await login.json()) as { accessToken: string }).accessToken
}

/** Every sale this shop has, paged to the end. `pos_e2e` is never dropped. */
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

    const body: { items: unknown[]; nextCursor: string | null; hasMore: boolean } =
      await response.json()

    total += body.items.length
    cursor = body.hasMore ? body.nextCursor : null
  } while (cursor !== null)

  return total
}

test.describe('offline', () => {
  test.beforeEach(async ({ page }) => {
    await enrolDevice(page)
    await signIn(page, 'cashier')
    await page.goto('/register')
    await ensureDrawerOpen(page)
    await waitForMirror(page)
  })

  test('a sale completes with the network off and is queued, not lost', async ({
    page,
    context,
  }) => {
    await context.setOffline(true)

    // The chip has to say so before anything else is believable: a cashier's
    // only signal that the till is on its own is this.
    await expect(page.getByTestId('sync-status')).toContainText('Offline', { timeout: 20_000 })

    // Scanned from the mirror. There is no server to ask.
    await scan(page, WATER)
    await expect(page.getByTestId('cart-line')).toHaveCount(1)
    await expect(page.getByTestId('cart-line')).toContainText(WATER_NAME)

    await page.getByTestId('take-cash').click()
    await expect(page.getByTestId('tender-panel')).toBeVisible()

    await page.keyboard.type('2.00', { delay: 0 })
    await page.keyboard.press('Enter')
    await page.getByRole('button', { name: /Complete/ }).click()

    /*
     * The panel that must never overstate itself. It says the sale is saved
     * *here*, and it has no sale number, because SaleSequence is assigned inside
     * the server's transaction and nothing offline can know it.
     */
    const queued = page.getByTestId('sale-queued')
    await expect(queued).toBeVisible()
    await expect(queued).toContainText('Saved on this till')

    // €1.20 priced locally, €2.00 tendered, €0.80 back — the same arithmetic the
    // server does, from the engine the conformance corpus pins to it.
    await expect(page.getByTestId('change-due')).toContainText('0.80')

    await expect(page.getByTestId('pending-sales')).toContainText('1 to send')
  })

  test('a sale survives a reload taken while offline and still lands exactly once', async ({
    page,
    context,
  }) => {
    /*
     * The property `sessionStorage` could not give and IndexedDB does. A tablet
     * on a counter gets reloaded, slept and crashed, and a sale a customer has
     * already paid for must not depend on a tab staying open.
     *
     * **Asserted by the sale arriving, not by the chip.** An earlier draft
     * reloaded and looked for "1 to send", and it failed for a reason worth
     * writing down rather than working around: reloading while offline cannot
     * restore the session, because the refresh token is exchanged with the
     * server and there is no server. The till therefore shows a re-auth prompt
     * rather than the register — which is exactly the limit invariant 11 buys
     * and 9.5 documents in the app.
     *
     * The queue is untouched by any of that. It is in IndexedDB, it belongs to
     * the till rather than to the tab, and this proves it by tearing the page
     * down and counting what the server ends up with.
     *
     * **The reload happens after the connection returns, and that is a limit of
     * this harness rather than a softer assertion.** Reloading *while* offline
     * needs the precached app shell, and `devOptions` is off — the specs drive
     * the dev server, which has no service worker, so `page.reload()` offline
     * fails with ERR_INTERNET_DISCONNECTED before any of this is reached. That
     * the shell is precached at all is asserted against the built worker in
     * `pwa.spec.ts`; the two together are what cover it.
     */
    const before = await saleCount(page)

    await context.setOffline(true)
    await expect(page.getByTestId('sync-status')).toContainText('Offline', { timeout: 20_000 })

    await scan(page, WATER)
    await page.getByTestId('take-cash').click()
    await page.keyboard.type('2.00', { delay: 0 })
    await page.keyboard.press('Enter')
    await page.getByRole('button', { name: /Complete/ }).click()

    await expect(page.getByTestId('pending-sales')).toContainText('1 to send')

    await context.setOffline(false)

    /*
     * The tablet is reloaded: every scrap of in-memory state is gone, including
     * the outbox's in-memory counters and the whole React tree.
     *
     * The *session* survives, because the refresh token is in `sessionStorage`
     * and a reload is not a tab close — so there is no second sign-in here.
     * (An earlier draft signed in again and raced a login form that had already
     * been replaced.) What is being proved is that the queue survives the
     * teardown, and it lives somewhere neither of those things touches.
     */
    await page.reload()

    await expect(page.getByTestId('sync-status')).toBeVisible({ timeout: 30_000 })

    /*
     * Polled against the server, not read once after waiting on the chip.
     *
     * The badge is hidden until the provider has counted, so it reads "nothing
     * pending" for a moment on every fresh page — an earlier draft waited for
     * that and passed instantly, then counted the sales before the replay had
     * run and reported the sale missing. The chip is a cashier's signal; the
     * server is the assertion.
     *
     * One sale, not zero and not two: nothing was lost to the teardown and
     * nothing was written twice by it.
     */
    await expect.poll(() => saleCount(page), { timeout: 30_000 }).toBe(before + 1)
  })

  test('reconnecting sends the queue exactly once', async ({ page, context }) => {
    const before = await saleCount(page)

    await context.setOffline(true)
    await expect(page.getByTestId('sync-status')).toContainText('Offline', { timeout: 20_000 })

    await scan(page, WATER)
    await page.getByTestId('take-cash').click()
    await page.keyboard.type('2.00', { delay: 0 })
    await page.keyboard.press('Enter')
    await page.getByRole('button', { name: /Complete/ }).click()

    await expect(page.getByTestId('pending-sales')).toContainText('1 to send')

    await context.setOffline(false)

    // The chip is the cashier's evidence that the queue drained, so it is what
    // the test waits on rather than a sleep.
    await expect(page.getByTestId('pending-sales')).toBeHidden({ timeout: 30_000 })
    await expect(page.getByTestId('sync-status')).toContainText('Online')

    /*
     * **Exactly one.** The assertion the whole phase is for.
     *
     * The sale carries its original Idempotency-Key, so a replay that raced or
     * repeated returns the first sale rather than writing a second — and if that
     * mechanism ever broke, this is the number that would say so.
     *
     * Polled rather than read once: the badge disappearing is the UI reacting,
     * and the server having the sale is the fact.
     */
    await expect.poll(() => saleCount(page), { timeout: 30_000 }).toBe(before + 1)
  })

  test('a replayed sale is dated when the customer paid, not when it synced', async ({
    page,
    context,
  }) => {
    /*
     * 9.0a, end to end. Without `occurredAt` the sale would be stamped at the
     * moment the network came back — landing in the wrong trading day, in the
     * wrong Z-report, against a drawer that may already have been counted.
     */
    const rungAt = Date.now()
    const before = await saleCount(page)

    await context.setOffline(true)
    await expect(page.getByTestId('sync-status')).toContainText('Offline', { timeout: 20_000 })

    await scan(page, WATER)
    await page.getByTestId('take-cash').click()
    await page.keyboard.type('2.00', { delay: 0 })
    await page.keyboard.press('Enter')
    await page.getByRole('button', { name: /Complete/ }).click()

    await expect(page.getByTestId('pending-sales')).toContainText('1 to send')

    // Long enough that a server-stamped time would be visibly different from
    // the moment the sale was rung.
    await page.waitForTimeout(3_000)

    await context.setOffline(false)

    // Waits for the sale to actually be there before reading it back.
    await expect.poll(() => saleCount(page), { timeout: 30_000 }).toBe(before + 1)

    const token = await ownerToken(page)
    const response = await page.request.get('/api/v1/sales?limit=1', {
      headers: { Authorization: `Bearer ${token}` },
    })

    const latest = (await response.json()) as { items: { id: string }[] }
    const detail = await page.request.get(`/api/v1/sales/${latest.items[0]!.id}`, {
      headers: { Authorization: `Bearer ${token}` },
    })

    const sale = (await detail.json()) as { completedAt: string; recordedAt: string }

    // Dated when the trade happened…
    expect(Math.abs(Date.parse(sale.completedAt) - rungAt)).toBeLessThan(15_000)

    // …and recorded later, which is the gap that says it was offline. Both are
    // needed: one is the reporting question, the other the reconciliation one.
    expect(Date.parse(sale.recordedAt)).toBeGreaterThan(Date.parse(sale.completedAt))
  })

  test('an unknown code offline is a banner, not a hang', async ({ page, context }) => {
    // No mirror hit and no server to ask. The cashier's next move is different
    // from a lookup that failed, so it is said differently.
    await context.setOffline(true)
    await expect(page.getByTestId('sync-status')).toContainText('Offline', { timeout: 20_000 })

    await scan(page, '9999999999999')

    await expect(page.getByTestId('unknown-code')).toBeVisible()
    await expect(page.getByTestId('cart-line')).toHaveCount(0)
  })
})
