import { expect, test } from '@playwright/test'
import { enrolRestaurantDevice, signIn, signInToRestaurant } from './fixtures/actors'

/**
 * A service, in a browser.
 *
 * **The walk-through the phase doc's Verification section asks for**, and the
 * reason 10.7 and 10.9 were one piece of work: per `docs/ROADMAP.md`, UI testing
 * is the thing that must never accumulate. Every endpoint underneath was already
 * proved against a real Postgres; what was never proved is that a person can
 * reach any of it.
 *
 * **One test, not six.** The obvious split — a test per screen — makes each one
 * depend on the last one's leftovers, and the database outlives the run: a
 * second execution starts with table 4 already seated and the assertions drift
 * out from under themselves. A service is one sequence anyway, so this walks it
 * once and leaves the table free at the end, which is also the state the next
 * run needs to find.
 *
 * Against its own tenant. `e2e-restaurant` exists so nothing here can disturb
 * `register.spec.ts` — and so the second test can assert the *retail* shop is
 * untouched while sharing a database with a restaurant, which is half of what
 * "the retail path is unchanged" has to mean.
 */
test.describe.configure({ mode: 'serial' })

test.describe('a restaurant', () => {
  test('is seated, ordered for, fired, bumped, settled and freed', async ({ page }) => {
    test.slow()

    await enrolRestaurantDevice(page)
    await signInToRestaurant(page, 'owner')

    // ── the floor ────────────────────────────────────────────────────────
    await page.goto('/register')

    // The room, not a cart. One route, decided by the shop's serviceMode.
    await expect(page.getByRole('heading', { name: 'Main room' })).toBeVisible()
    await expect(page.getByRole('link', { name: 'Floor' })).toBeVisible()
    await expect(page.getByRole('link', { name: 'Kitchen' })).toBeVisible()

    await page.getByTestId('table-4').click()
    await page.waitForURL(/\/restaurant\/orders\//)

    const orderUrl = page.url()

    // ── the order, and a question that has to be answered ────────────────
    //
    // The burger asks "Cooked how?", which is required. The sheet's gating is a
    // courtesy — ModifierRules refuses the line regardless — but the waiter has
    // to find out at the table rather than after pressing send.
    await page.getByLabel('Search the menu').fill('Beef Burger')
    await page.getByRole('button', { name: 'Beef Burger', exact: true }).click()

    const sheet = page.getByTestId('modifier-sheet')
    await expect(sheet).toBeVisible()
    await expect(page.getByTestId('confirm-modifiers')).toBeDisabled()

    await sheet.getByRole('button', { name: 'Medium' }).click()
    await expect(page.getByTestId('confirm-modifiers')).toBeEnabled()
    await page.getByTestId('confirm-modifiers').click()

    const course = page.getByRole('region', { name: 'Course 1' })

    // Nested under its parent, not listed beside it.
    await expect(course.getByText('Beef Burger')).toBeVisible()
    await expect(course.getByText('Medium')).toBeVisible()

    // No total on this screen. OrderLine stores inputs and not amounts, so a
    // figure here would be a second set of numbers drifting from the sale.
    await expect(course.getByText(/Total/i)).toHaveCount(0)

    // ── fire ─────────────────────────────────────────────────────────────
    await page.getByTestId('fire-course-1').click()

    // Away, and the button says so rather than offering to send it again.
    await expect(page.getByTestId('fire-course-1')).toHaveText('Away')

    // ── the pass ─────────────────────────────────────────────────────────
    await page.goto('/kitchen')

    // Mains route to the grill, overriding what Food says. The station is picked
    // once per screen and kept in sessionStorage — a fresh context has none,
    // which is the storage behaving as designed.
    await page.getByRole('button', { name: 'Grill' }).click()
    await expect(page.getByRole('heading', { name: 'GRILL' })).toBeVisible()

    const ticket = page.getByRole('region', { name: /Table 4/ }).first()

    await expect(ticket).toBeVisible()
    await expect(ticket.getByText(/Beef Burger/)).toBeVisible()

    // A modifier is a phrase under the item, not a row of its own.
    await expect(ticket.getByText('Medium')).toBeVisible()

    await page.getByTestId('bump').first().click()

    await page.getByRole('button', { name: 'Show cleared' }).click()
    await expect(page.getByTestId('recall').first()).toBeVisible()

    // ── a plate comes off ────────────────────────────────────────────────
    await page.goto(orderUrl)

    // Fired, so it needs a reason. The Owner holds CanVoidFiredLine, so no PIN
    // is asked for — a Cashier would get the manager dialog instead.
    await page.getByRole('button', { name: 'Remove Beef Burger' }).click()

    const voidDialog = page.getByTestId('void-line')
    await expect(voidDialog).toBeVisible()
    await expect(page.getByTestId('confirm-void')).toBeDisabled()

    await voidDialog.getByLabel('Reason').fill('Overcooked')
    await page.getByTestId('confirm-void').click()

    await expect(course.getByText('Beef Burger')).toHaveCount(0)

    // Still on the ticket, struck through. The chef may already have plated it,
    // and a line that vanished would erase the evidence that the shop lost one.
    await page.goto('/kitchen')
    await page.getByRole('button', { name: 'Show cleared' }).click()
    await expect(page.getByText(/Beef Burger/).first()).toHaveClass(/line-through/)

    // ── something to actually pay for, and the bill ──────────────────────
    await page.goto(orderUrl)

    await page.getByLabel('Search the menu').fill('Sparkling Water')
    await page.getByRole('button', { name: 'Sparkling Water', exact: true }).click()
    await page.getByTestId('confirm-modifiers').click()

    await page.getByRole('button', { name: 'Bill' }).click()
    await page.waitForURL(/\/bill$/)

    // A drawer, if this till has not opened one. The floor deliberately does not
    // offer this — a floor is not a till — so the bill screen is where a
    // restaurant discovers it needs one, and where it can do something about it.
    //
    // Waited for rather than probed: the shift query is in flight when this
    // screen first renders, and a bare isVisible() reads "no drawer needed" from
    // a panel that simply has not appeared yet.
    const drawer = page.getByRole('region', { name: 'Open the drawer' })

    const needsDrawer = await drawer
      .waitFor({ state: 'visible', timeout: 10_000 })
      .then(() => true)
      .catch(() => false)

    if (needsDrawer) {
      await drawer.getByLabel(/opening float/i).fill('100')
      await drawer.getByRole('button', { name: 'Open the drawer' }).click()
      await expect(drawer).toBeHidden()
    }

    await page.getByRole('button', { name: 'Bill the whole table' }).click()
    await expect(page.getByTestId('bill-1')).toBeVisible()

    await page.getByLabel('Tip').fill('2.00')

    // Quick cash, sized to cover the bill and the tip together.
    await page.getByRole('button', { name: /^€\d/ }).first().click()
    await page
      .getByRole('button', { name: /Complete/i })
      .first()
      .click()

    // Change is the server's figure, carried on the pay response — not fetched
    // afterwards at the one moment somebody is counting notes into a hand.
    await expect(page.getByTestId('bill-paid')).toBeVisible()
    await expect(page.getByText('Tip')).toBeVisible()

    // ── and the table is free again ──────────────────────────────────────
    await page.getByRole('button', { name: /back to the floor/i }).click()
    await page.waitForURL('/register')

    // Which is also the state the next run has to find, and why this test
    // settles what it opened rather than leaving it for a later one.
    await expect(page.getByTestId('table-4')).toContainText('Seats')
  })

  test('leaves the retail shop completely unchanged', async ({ page }) => {
    // The other half of 10.7's first exit criterion, and it is as much about
    // what did not change as what did. This shop shares a database with a
    // restaurant that has just traded through a whole service.
    await signIn(page, 'owner')

    await page.goto('/register')

    await expect(page.getByRole('link', { name: 'Register' })).toBeVisible()
    await expect(page.getByRole('link', { name: 'Floor' })).toHaveCount(0)
    await expect(page.getByRole('link', { name: 'Kitchen' })).toHaveCount(0)

    // The till, with a cart on it — not a floor plan.
    await expect(page.getByRole('heading', { name: 'Main room' })).toHaveCount(0)
  })
})
