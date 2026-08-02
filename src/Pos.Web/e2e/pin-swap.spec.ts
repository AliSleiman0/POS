import { expect, test } from '@playwright/test'
import { enrolDevice, signIn } from './fixtures/actors'
import { CASHIER_PIN, readSeed } from './fixtures/seed'

/**
 * Device enrolment and the PIN swap — the flow a shop actually uses all day.
 *
 * Each test gets a fresh browser context, so `localStorage` does not carry
 * between them and every spec here arranges its own device token. The first one
 * does it through the enrolment screen because that screen is what it is
 * testing; the others use `enrolDevice`.
 */

test.describe('till', () => {
  test('PIN login is unavailable until the device is enrolled', async ({ page }) => {
    await page.goto('/pin')

    // A PIN is never sufficient authentication on its own: it authenticates a
    // person to an already-trusted *device*, and a 4-digit secret without that
    // is trivially brute-forced. So an unenrolled browser is told plainly
    // rather than being allowed to try.
    await expect(page.getByText('This device is not enrolled.')).toBeVisible()
    await expect(page.getByLabel('PIN')).toHaveCount(0)
  })

  test('an owner enrols the device and a cashier swaps in with a PIN', async ({ page }) => {
    const seed = readSeed()

    await signIn(page, 'owner')
    await page.goto('/settings/device')

    // The paste path, which is what you use when a tablet was wiped and the
    // token came from the seeder or an earlier enrolment.
    await page.getByLabel('Register id').fill(seed.registerId)
    await page.getByLabel('Device token').fill(seed.deviceToken)
    await page.getByRole('button', { name: 'Store token' }).click()

    await expect(page.getByText('Enrolled as a till')).toBeVisible()

    // ---- the swap -------------------------------------------------------
    await page.goto('/pin')

    // The staff list comes from GET /employees/pin-eligible, which is
    // authenticated by the device token rather than the owner's bearer token.
    await page.getByRole('button', { name: 'Robin Vale' }).click()
    await page.getByLabel('PIN').fill(CASHIER_PIN)
    await page.getByRole('button', { name: 'Start shift session' }).click()

    await page.waitForURL('/')

    // Swapped, not logged out and back in: a different person is now on the
    // till, with a cashier's policies. Scoped to the header rather than the
    // page, because the name also appears in the greeting below it.
    const header = page.getByRole('banner')
    await expect(header).toContainText('Robin Vale')
    await expect(header).toContainText('Cashier')

    // The catalog links were there a moment ago, as the owner. They are gone
    // without a reload, which is what makes this a swap rather than a re-login.
    await expect(page.getByRole('link', { name: 'Catalog' })).toHaveCount(0)
  })

  test('a wrong PIN is refused without saying which part was wrong', async ({ page }) => {
    await enrolDevice(page)
    await page.goto('/pin')

    // Sam Cole, not Robin Vale. A failed PIN burns one of that user's five
    // lockout attempts, and Robin Vale is the account the authorization specs
    // sign in as — with `retries: 2` in CI, spending attempts on them could
    // fail an unrelated spec depending on the order the workers happened to run.
    await page.getByRole('button', { name: 'Sam Cole' }).click()
    await page.getByLabel('PIN').fill('0000')
    await page.getByRole('button', { name: 'Start shift session' }).click()

    await expect(page.getByRole('alert')).toContainText(/not recognised|Too many/i)
    await expect(page).toHaveURL(/\/pin/)
  })
})
