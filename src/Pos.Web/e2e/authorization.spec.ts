import { expect, test } from '@playwright/test'
import { signIn } from './fixtures/actors'

/**
 * What a Cashier cannot see, and what the server does not send them.
 *
 * This is also the test that covers the one accepted gap in `auth/policies.ts`:
 * that list of policy names is hand-written and could drift from
 * `PolicyCatalog`. If a policy is renamed server-side, the Cashier's `/auth/me`
 * stops matching and these assertions change — which is the drift becoming
 * visible rather than a control silently turning into a no-op.
 */
test.describe('authorization', () => {
  test('a cashier gets no catalog controls', async ({ page }) => {
    await signIn(page, 'cashier')

    // `CanManageCatalog` is Manager and Owner only.
    await expect(page.getByRole('link', { name: 'Catalog' })).toHaveCount(0)
    await expect(page.getByRole('link', { name: 'Manage catalog' })).toHaveCount(0)

    // The route itself refuses in place, rather than redirecting somewhere that
    // leaves the user wondering whether the click registered.
    await page.goto('/catalog')
    await expect(page.getByRole('alert')).toContainText('do not have access')
  })

  test('a cashier holds exactly one policy', async ({ page }) => {
    await signIn(page, 'cashier')

    // Straight from GET /auth/me. Mirrors the backend's
    // AuthorizationContractTests.Each_role_grants_the_expected_number_of_policies.
    await expect(page.getByText('CanSell', { exact: true })).toBeVisible()
    await expect(page.getByText('CanManageCatalog', { exact: true })).toHaveCount(0)
    await expect(page.getByText('CanViewMargins', { exact: true })).toHaveCount(0)
  })

  test('cost price is absent for a manager and present for an owner', async ({ page }) => {
    // `CanViewMargins` is Owner-only: a manager who can read cost prices can
    // price-shop the shop's suppliers. The UI gate is defence in depth — the
    // server's SQL for a manager never names cost_price and the field is
    // omitted from the JSON entirely, which is the actual control.
    await signIn(page, 'manager')
    await page.goto('/catalog/products/new')
    await expect(page.getByLabel('Cost price')).toHaveCount(0)

    await page.getByRole('button', { name: 'Sign out' }).click()
    await page.waitForURL(/\/login/)

    await signIn(page, 'owner')
    await page.goto('/catalog/products/new')
    await expect(page.getByLabel('Cost price')).toBeVisible()
  })

  test('an unauthenticated visitor is sent to the login page', async ({ page }) => {
    await page.goto('/catalog')

    await expect(page).toHaveURL(/\/login/)
    await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible()
  })

  test('wrong credentials give one message, whatever was wrong', async ({ page }) => {
    await page.goto('/login')

    await page.getByLabel('Shop').fill('no-such-shop-at-all')
    await page.getByLabel('Email').fill('nobody@example.test')
    await page.getByLabel('Password').fill('wrong-password-entirely')
    await page.getByRole('button', { name: 'Sign in' }).click()

    // The server answers unknown slug, unknown email and wrong password
    // identically and in comparable time, so it is not an enumeration oracle.
    // A client that distinguished them would give that away.
    await expect(page.getByRole('alert')).toContainText(/incorrect/i)
    await expect(page).toHaveURL(/\/login/)
  })
})
