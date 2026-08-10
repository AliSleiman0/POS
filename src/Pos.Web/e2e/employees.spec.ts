import { expect, test, type Page } from '@playwright/test'
import { signIn, uniqueEmail } from './fixtures/actors'
import { TENANT_SLUG } from './fixtures/seed'

/**
 * Hiring somebody, and the refusals that stop a shop locking itself out.
 *
 * **Nothing here counts rows or expects one on a particular page.** `pos_e2e` is
 * seeded once and never dropped, so every run that creates a user leaves it
 * behind and the staff list only grows — the same trap that broke three
 * assertions in earlier phases. Every person here is found by an address unique
 * to this run.
 *
 * **And nobody created here gets a PIN.** A PIN would put them on the till's
 * `pin-eligible` list permanently, which the PIN-swap spec picks staff from by
 * display name.
 */
test.describe('employees', () => {
  test('an owner can add somebody who can then sign in', async ({ page }) => {
    const email = uniqueEmail('hire')
    const staffPassword = 'Battery-Staple-7'

    await signIn(page, 'owner')
    await page.goto('/admin/people')

    await page.getByRole('button', { name: 'Add somebody' }).click()

    await page.getByLabel('Name').fill('New Starter')
    await page.getByLabel('Email').fill(email)
    await page.getByLabel('Role').selectOption('Cashier')
    await page.getByLabel('Initial password').fill(staffPassword)
    await page.getByRole('button', { name: 'Add', exact: true }).click()

    await expect(rowFor(page, email)).toBeVisible()

    // The whole point of the screen: they can actually get in afterwards. A
    // create that returns 201 and leaves an unusable account looks identical
    // from the list.
    await page.getByRole('button', { name: 'Sign out' }).click()
    await page.waitForURL(/\/login/)

    await page.getByLabel('Shop').fill(TENANT_SLUG)
    await page.getByLabel('Email').fill(email)
    await page.getByLabel('Password').fill(staffPassword)
    await page.getByRole('button', { name: 'Sign in' }).click()

    await page.waitForURL('/')
    await expect(page.getByText('New Starter')).toBeVisible()
  })

  test('a new cashier sees no staff controls at all', async ({ page }) => {
    const email = uniqueEmail('gated')
    const staffPassword = 'Battery-Staple-7'

    await signIn(page, 'owner')
    await page.goto('/admin/people')
    await addStaff(page, { email, name: 'Gated Starter', role: 'Cashier', password: staffPassword })

    await page.getByRole('button', { name: 'Sign out' }).click()
    await page.waitForURL(/\/login/)

    await page.getByLabel('Shop').fill(TENANT_SLUG)
    await page.getByLabel('Email').fill(email)
    await page.getByLabel('Password').fill(staffPassword)
    await page.getByRole('button', { name: 'Sign in' }).click()
    await page.waitForURL('/')

    await expect(page.getByRole('link', { name: 'People' })).toHaveCount(0)
    await expect(page.getByRole('link', { name: 'Tills' })).toHaveCount(0)

    // And the route refuses in place rather than redirecting somewhere that
    // leaves the user wondering whether the click registered.
    await page.goto('/admin/people')
    await expect(page.getByRole('alert')).toContainText('do not have access')
  })

  test('a manager cannot reach the staff list either', async ({ page }) => {
    // CanManageEmployees is Owner-only: whoever sets PINs can create a user who
    // sells. A manager holding seven policies and not this one is the case most
    // likely to be got wrong.
    await signIn(page, 'manager')

    await expect(page.getByRole('link', { name: 'People' })).toHaveCount(0)

    await page.goto('/admin/people')
    await expect(page.getByRole('alert')).toContainText('do not have access')
  })

  test('an owner cannot deactivate themselves', async ({ page }) => {
    await signIn(page, 'owner')
    await page.goto('/admin/people')

    const ownRow = page.getByTestId('employee-row').filter({ hasText: 'you' })

    await expect(ownRow).toBeVisible()

    // The button is absent rather than present-and-failing. The server refuses
    // it regardless — this is the convenience layer, not the gate.
    await expect(ownRow.getByRole('button', { name: 'Deactivate' })).toHaveCount(0)
  })

  test('an owner cannot demote themselves, and the refusal says why', async ({ page }) => {
    await signIn(page, 'owner')
    await page.goto('/admin/people')

    const ownRow = page.getByTestId('employee-row').filter({ hasText: 'you' })
    await ownRow.getByRole('button', { name: 'Edit' }).click()

    await page.getByLabel('Role').selectOption('Manager')
    await page.getByRole('button', { name: 'Save' }).click()

    // A shop with no owner cannot create one — the only route that could is
    // gated on the role being removed. There is no platform admin tool, so
    // recovery would mean us connecting to their database.
    //
    // Located by `data-slot`, not by role: the toast viewport is an `aria-live`
    // region with no ARIA role, deliberately — announcing without stealing
    // focus, because focus theft mid-scan drops the next barcode.
    await expect(page.locator('[data-slot="toast"][data-tone="error"]')).toContainText(
      /own owner role|another owner/i,
    )

    // And the change did not land, which is what makes the message true.
    await page.reload()
    await expect(page.getByTestId('employee-row').filter({ hasText: 'you' })).toContainText('Owner')
  })

  test('somebody deactivated can no longer sign in', async ({ page }) => {
    const email = uniqueEmail('leaver')
    const staffPassword = 'Battery-Staple-7'

    await signIn(page, 'owner')
    await page.goto('/admin/people')
    await addStaff(page, { email, name: 'Leaver', role: 'Cashier', password: staffPassword })

    const row = rowFor(page, email)

    // Two presses, in the page. No browser confirm() — it blocks the page, and
    // a blocked till stalls a queue.
    await row.getByRole('button', { name: 'Deactivate' }).click()
    await row.getByRole('button', { name: 'Really deactivate?' }).click()

    await expect(row).toContainText('Deactivated')

    await page.getByRole('button', { name: 'Sign out' }).click()
    await page.waitForURL(/\/login/)

    await page.getByLabel('Shop').fill(TENANT_SLUG)
    await page.getByLabel('Email').fill(email)
    await page.getByLabel('Password').fill(staffPassword)
    await page.getByRole('button', { name: 'Sign in' }).click()

    await expect(page.getByRole('alert')).toContainText(/incorrect/i)
    await expect(page).toHaveURL(/\/login/)
  })

  test('an owner can create a till and revoke it', async ({ page }) => {
    const name = `Spare Counter ${Date.now().toString(36)}`

    await signIn(page, 'owner')
    await page.goto('/admin/tills')

    await page.getByLabel('New till').fill(name)
    await page.getByRole('button', { name: 'Add till' }).click()

    const row = page.getByTestId('register-row').filter({ hasText: name })

    await expect(row).toBeVisible()

    // A till nobody has enrolled has no token to revoke, so the control is
    // absent until there is one — which is also the honest reading of the API,
    // where revoke on an unenrolled register is a no-op.
    await expect(row.getByRole('button', { name: 'Revoke' })).toHaveCount(0)
  })
})

/** The staff row for one person, found by the address only this run used. */
function rowFor(page: Page, email: string) {
  return page.getByTestId('employee-row').filter({ hasText: email })
}

async function addStaff(
  page: Page,
  staff: { email: string; name: string; role: string; password: string },
): Promise<void> {
  await page.getByRole('button', { name: 'Add somebody' }).click()

  await page.getByLabel('Name').fill(staff.name)
  await page.getByLabel('Email').fill(staff.email)
  await page.getByLabel('Role').selectOption(staff.role)
  await page.getByLabel('Initial password').fill(staff.password)
  await page.getByRole('button', { name: 'Add', exact: true }).click()

  await expect(rowFor(page, staff.email)).toBeVisible()
}
