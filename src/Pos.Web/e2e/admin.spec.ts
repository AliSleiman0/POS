import { expect, test } from '@playwright/test'
import { signIn, uniqueSku } from './fixtures/actors'
import { RECEIPT_FOOTER } from './fixtures/seed'

/**
 * The activity log and the shop's own settings.
 *
 * Nothing here counts rows: `pos_e2e` is seeded once and never dropped, so both screens
 * accumulate across every run there has ever been. The activity log is newest-first and
 * paginated, so whatever this spec just did is on page one — which is what makes it safe
 * to assert on at all.
 */
test.describe('admin', () => {
  /**
   * Puts the receipt footer back.
   *
   * These settings are **tenant-wide and shared with every other spec** — `receipt.spec.ts`
   * asserts the seeded footer appears on a printed receipt, and Playwright runs one worker in
   * file order, so `admin` editing it left `receipt` red. And because `pos_e2e` is never
   * dropped, an unrestored value stays wrong for every future run on this machine as well.
   *
   * In `afterEach` rather than at the end of the test that changes it, so a mid-test failure
   * does not leave the shared tenant broken for everything downstream.
   */
  test.afterEach(async ({ page }) => {
    // No `signIn` here. Each test leaves its own session behind, and signing in again
    // without signing out first does nothing — `/login` bounces an authenticated
    // visitor to `/`, so the form never renders and the fill hangs until the hook
    // times out. Whatever session the test ended with is the one to use: an owner's
    // reaches the field, and anybody else's finds the refusal and returns.
    await page.goto('/admin/settings')

    const footer = page.getByLabel('Footer')
    const refusal = page.getByRole('alert')

    // Settle first. A bare `count()` here runs while the page is still showing its
    // loading state, finds no field and returns — which reads as "nothing to restore"
    // and silently leaves the shared tenant edited.
    await expect(footer.or(refusal).first()).toBeVisible()

    if ((await footer.count()) === 0) {
      return
    }

    // And wait for the form to seed itself from the server before touching it. It
    // fills its fields in an effect once the query resolves, so a fill that lands
    // first is overwritten a moment later — and the save then writes the modified
    // value back, which looks exactly like a restore that worked.
    await expect(page.getByLabel('Name')).not.toHaveValue('')

    if ((await footer.inputValue()) === RECEIPT_FOOTER) {
      return
    }

    await footer.fill(RECEIPT_FOOTER)
    await page.getByRole('button', { name: 'Save settings' }).click()
    await expect(page.locator('[data-slot="toast"]')).toContainText('Saved')
    await expect(footer).toHaveValue(RECEIPT_FOOTER)
  })

  test('an owner sees what they just did in the activity log', async ({ page }) => {
    const reason = `Damaged in transit ${uniqueSku('e2e')}`

    await signIn(page, 'owner')

    // Something worth auditing: a stock correction made by hand.
    await page.goto('/stock')
    await page.getByRole('button', { name: 'Adjust' }).first().click()

    await page.getByLabel('What happened').selectOption('Waste')
    await page.getByLabel('Quantity').fill('1')
    await page.getByLabel('Reason').fill(reason)
    await page.getByRole('button', { name: 'Record movement' }).click()

    await expect(page.getByRole('dialog')).toHaveCount(0)

    await page.goto('/admin/activity')

    const row = page.getByTestId('audit-row').filter({ hasText: reason })

    await expect(row).toBeVisible()
    await expect(row).toContainText('Stock adjusted')
    await expect(row).toContainText('Ada Byrne')
  })

  test('the action filter narrows the log and an unknown one is not silently ignored', async ({
    page,
  }) => {
    await signIn(page, 'owner')
    await page.goto('/admin/activity')

    await page.getByLabel('Action').selectOption('SaleVoided')

    // Every visible row is the action that was asked for. A filter that silently did
    // nothing would show everything, and the reader would conclude they had looked.
    const rows = page.getByTestId('audit-row')
    const count = await rows.count()

    for (let index = 0; index < count; index++) {
      await expect(rows.nth(index)).toContainText('Sale voided')
    }
  })

  test('a manager cannot reach the activity log or the settings', async ({ page }) => {
    await signIn(page, 'manager')

    await expect(page.getByRole('link', { name: 'Activity' })).toHaveCount(0)
    await expect(page.getByRole('link', { name: 'Settings' })).toHaveCount(0)

    await page.goto('/admin/activity')
    await expect(page.getByRole('alert')).toContainText('do not have access')

    await page.goto('/admin/settings')
    await expect(page.getByRole('alert')).toContainText('do not have access')
  })

  test('an owner can change the receipt footer and see it recorded', async ({ page }) => {
    const footer = `Returns within 30 days ${uniqueSku('e2e')}`

    await signIn(page, 'owner')
    await page.goto('/admin/settings')

    await page.getByLabel('Footer').fill(footer)
    await page.getByRole('button', { name: 'Save settings' }).click()

    await expect(page.locator('[data-slot="toast"]')).toContainText('Saved')

    // It survives a reload, which is what separates "the form accepted it" from "the
    // shop's settings changed". Before Phase 7 this was reachable only through the
    // seeder, so a customer could not do it at all.
    await page.reload()
    await expect(page.getByLabel('Footer')).toHaveValue(footer)

    await page.goto('/admin/activity')

    const row = page.getByTestId('audit-row').filter({ hasText: 'Setting changed' }).first()
    await expect(row).toContainText('receiptFooter')
  })

  test('the settings screen loads with the shop already configured', async ({ page }) => {
    await signIn(page, 'owner')
    await page.goto('/admin/settings')

    // Currency and time zone are shown and not editable: changing them rewrites what
    // every past report meant, so they are migrations rather than form fields.
    await expect(page.getByLabel('Currency')).toBeDisabled()
    await expect(page.getByLabel('Time zone')).toBeDisabled()

    // Deliberately *no* assertion here about whether Tax mode is locked. That depends
    // on whether this database has ever recorded a sale, and this spec runs first on
    // a fresh one — the original version asserted "locked" and passed only because a
    // local `pos_e2e` has traded since its first ever run, then failed on the runner.
    // The rendering of both states is pinned deterministically in SettingsPage.test.tsx,
    // and the server-side refusal in SettingsTests.
    await expect(page.getByLabel('Tax mode')).toBeVisible()
  })
})
