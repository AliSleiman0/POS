import { expect, test } from '@playwright/test'
import { signIn, uniqueBarcode, uniqueSku } from './fixtures/actors'

/**
 * The Phase 4 verification flow, end to end against a real API and a real
 * Postgres: log in → create a product with two barcodes → search and find it →
 * edit the price → adjust stock with a reason → see the movement in the ledger.
 */
test.describe('catalog', () => {
  test('an owner can take a product through its whole life', async ({ page }) => {
    const sku = uniqueSku('E2E')
    const name = `Test product ${sku}`
    const barcodeOne = uniqueBarcode()
    const barcodeTwo = uniqueBarcode()

    await signIn(page, 'owner')

    // ---- create ---------------------------------------------------------
    await page.goto('/catalog/products/new')

    await page.getByLabel('SKU').fill(sku)
    await page.getByLabel('Name').fill(name)
    await page.getByLabel('Unit price').fill('4.50')
    // A tax class is required and the form preselects the default, so this only
    // asserts that it did rather than choosing one.
    await expect(page.getByLabel('Tax class')).not.toHaveValue('')

    await page.getByRole('button', { name: 'Create product' }).click()

    // Redirected to the saved product, which is how we know it has an id.
    await expect(page).toHaveURL(/\/catalog\/products\/[0-9a-f-]{36}$/)
    await expect(page.getByRole('heading', { name })).toBeVisible()

    // ---- two barcodes ---------------------------------------------------
    // Several per product is the point: a multipack and a single carry
    // different codes for the same item.
    for (const code of [barcodeOne, barcodeTwo]) {
      await page.getByLabel('Scan or type a code').fill(code)
      await page.getByRole('button', { name: 'Add', exact: true }).click()
      await expect(page.getByText(code)).toBeVisible()
    }

    // ---- search ---------------------------------------------------------
    await page.goto('/catalog')
    await page.getByLabel('Search products').fill(sku)

    const row = page.getByRole('row').filter({ hasText: sku })
    await expect(row).toBeVisible()
    await expect(row).toContainText('€4.50')

    // ---- edit the price -------------------------------------------------
    await row.getByRole('link', { name }).click()
    await page.getByLabel('Unit price').fill('5.25')
    await page.getByRole('button', { name: 'Save changes' }).click()
    await expect(page.getByText('Product saved.')).toBeVisible()

    await page.goto('/catalog')
    await page.getByLabel('Search products').fill(sku)
    await expect(page.getByRole('row').filter({ hasText: sku })).toContainText('€5.25')

    // ---- adjust stock, with a reason ------------------------------------
    await page.goto('/stock')
    await page.getByLabel('Search stock').fill(sku)

    const stockRow = page.getByRole('row').filter({ hasText: sku })
    await expect(stockRow).toBeVisible()
    await stockRow.getByRole('button', { name: 'Adjust' }).click()

    const dialog = page.getByRole('dialog')
    await dialog.getByLabel('Quantity').fill('12')

    // The reason is required in the UI as well as at the API: in six months
    // this row is the only explanation of why the number changed.
    await dialog.getByRole('button', { name: 'Record movement' }).click()
    await expect(dialog.getByText(/reason is required/i)).toBeVisible()

    await dialog.getByLabel('Reason').fill('Delivery note 4471')
    await dialog.getByRole('button', { name: 'Record movement' }).click()

    await expect(page.getByText(/is now 12 on hand/)).toBeVisible()

    // ---- the movement is in the ledger ----------------------------------
    await page.getByLabel('Search stock').fill(sku)
    await page
      .getByRole('row')
      .filter({ hasText: sku })
      .getByRole('button', { name: 'Ledger' })
      .click()

    const ledger = page.getByRole('dialog')
    await expect(ledger.getByRole('heading', { name: 'Stock ledger' })).toBeVisible()
    await expect(ledger.getByRole('row').filter({ hasText: 'Delivery note 4471' })).toContainText(
      '+12',
    )
  })

  test('a duplicate SKU is reported against the field, not as a crash', async ({ page }) => {
    const sku = uniqueSku('DUP')

    await signIn(page, 'owner')

    for (const attempt of [1, 2]) {
      await page.goto('/catalog/products/new')
      await page.getByLabel('SKU').fill(sku)
      await page.getByLabel('Name').fill(`Duplicate attempt ${String(attempt)}`)
      await page.getByLabel('Unit price').fill('1.00')
      await page.getByRole('button', { name: 'Create product' }).click()

      if (attempt === 1) {
        await expect(page).toHaveURL(/\/catalog\/products\/[0-9a-f-]{36}$/)
      }
    }

    // A 409 with a stable `type` and no `errors` map, rendered on the SKU field
    // by branching on the type rather than parsing the message.
    await expect(page.getByText(/already/i)).toBeVisible()
    await expect(page).toHaveURL(/\/catalog\/products\/new$/)
  })

  test('an empty search says so instead of showing a blank panel', async ({ page }) => {
    await signIn(page, 'owner')
    await page.goto('/catalog')

    await page.getByLabel('Search products').fill('no-such-product-anywhere-xyzzy')

    await expect(page.getByText('Nothing matched.')).toBeVisible()
  })
})
