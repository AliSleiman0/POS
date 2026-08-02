/**
 * Client validation that **mirrors** the server's, and does not replace it.
 *
 * The server is the authority — `ProductEndpoints.ValidateAsync`,
 * `CatalogRules.IsStorableAmount`, and a `numeric(19,4)` column behind both.
 * Everything here exists only to save a round trip on a mistake the user can
 * see, and every rule below has a counterpart there that runs regardless.
 *
 * The limits are duplicated constants, not derived ones, so they can drift.
 * What catches that is the shape of the failure: a value this accepts and the
 * server rejects comes back as a `problem+json` with a per-field `errors` map,
 * and the form renders that against the same field. So the worst case is one
 * wasted round trip and a correct message — never a silently accepted value.
 */

/** `Product.SkuMaxLength`. */
export const SKU_MAX_LENGTH = 64
/** `Product.NameMaxLength`. */
export const PRODUCT_NAME_MAX_LENGTH = 200
/** `Product.DescriptionMaxLength`. */
export const DESCRIPTION_MAX_LENGTH = 1000
/** `Barcode.CodeMaxLength`. */
export const BARCODE_MAX_LENGTH = 64
/** `Category.NameMaxLength`. */
export const CATEGORY_NAME_MAX_LENGTH = 100
/** `TaxClass.NameMaxLength`. */
export const TAX_CLASS_NAME_MAX_LENGTH = 60

/** The scale of `numeric(19,4)`. More decimals than this do not survive storage. */
const AMOUNT_SCALE = 4

/** The scale of `numeric(6,4)` on `tax_class.rate`. */
const RATE_SCALE = 4

export type FieldErrors = Record<string, string>

/**
 * Whether a typed amount is one the server will store exactly.
 *
 * Non-negative, and at most four decimal places. Deliberately operates on the
 * **string** the user typed rather than a parsed number: `parseFloat` would
 * turn `1.00005` into a value whose decimal count is no longer inspectable, and
 * would accept `1e3` as a price.
 */
export function isStorableAmount(input: string): boolean {
  const match = /^(?:0|[1-9]\d*)(?:\.(\d+))?$/.exec(input.trim())

  if (match === null) {
    return false
  }

  return (match[1]?.length ?? 0) <= AMOUNT_SCALE
}

/**
 * Whether a tax rate is one `ck_tax_class_rate_range` will accept.
 *
 * Inclusive 0 to 1. A rate is a *fraction*: 20% is `0.2`, and `20` is four
 * hundred times too much rather than a unit mix-up worth guessing at — see
 * `CatalogRules.IsValidTaxRate`.
 */
export function isValidTaxRate(input: string): boolean {
  const trimmed = input.trim()
  const match = /^(?:0|1)(?:\.(\d+))?$/.exec(trimmed)

  if (match === null || (match[1]?.length ?? 0) > RATE_SCALE) {
    return false
  }

  return Number(trimmed) <= 1
}

/** Required, trimmed, and within the column's width. */
function checkText(
  value: string,
  maxLength: number,
  label: string,
  required = true,
): string | undefined {
  const trimmed = value.trim()

  if (trimmed.length === 0) {
    return required ? `A ${label} is required.` : undefined
  }

  if (trimmed.length > maxLength) {
    return `A ${label} of at most ${String(maxLength)} characters is required.`
  }

  return undefined
}

export interface ProductFormValues {
  sku: string
  name: string
  description: string
  categoryId: string
  taxClassId: string
  unitPrice: string
  costPrice: string
  unit: string
  trackStock: boolean
}

export function validateProduct(values: ProductFormValues): FieldErrors {
  const errors: FieldErrors = {}

  // Every field is checked before returning, so a form wrong in three places
  // reports all three — the same contract the server's ValidateAsync honours.
  const sku = checkText(values.sku, SKU_MAX_LENGTH, 'SKU')
  if (sku !== undefined) errors['sku'] = sku

  const name = checkText(values.name, PRODUCT_NAME_MAX_LENGTH, 'name')
  if (name !== undefined) errors['name'] = name

  const description = checkText(values.description, DESCRIPTION_MAX_LENGTH, 'description', false)
  if (description !== undefined) errors['description'] = description

  if (values.unitPrice.trim() === '') {
    errors['unitPrice'] = 'A unit price is required.'
  } else if (!isStorableAmount(values.unitPrice)) {
    errors['unitPrice'] = 'A price of 0 or more with at most 4 decimal places is required.'
  }

  // Validated even when it will be dropped: a caller without CanViewMargins is
  // told their number was malformed rather than having it silently ignored.
  if (values.costPrice.trim() !== '' && !isStorableAmount(values.costPrice)) {
    errors['costPrice'] = 'A cost of 0 or more with at most 4 decimal places is required.'
  }

  if (values.taxClassId === '') {
    // A product with no tax class cannot be priced at all.
    errors['taxClassId'] = 'A tax class is required.'
  }

  return errors
}

export function validateBarcode(code: string): string | undefined {
  return checkText(code, BARCODE_MAX_LENGTH, 'barcode')
}

export function validateCategoryName(name: string): string | undefined {
  return checkText(name, CATEGORY_NAME_MAX_LENGTH, 'name')
}

export function validateTaxClass(name: string, rate: string): FieldErrors {
  const errors: FieldErrors = {}

  const nameError = checkText(name, TAX_CLASS_NAME_MAX_LENGTH, 'name')
  if (nameError !== undefined) errors['name'] = nameError

  if (rate.trim() === '') {
    errors['rate'] = 'A rate is required.'
  } else if (!isValidTaxRate(rate)) {
    errors['rate'] =
      'A rate between 0 and 1 with at most 4 decimal places is required — 20% is 0.2.'
  }

  return errors
}

/**
 * Merges the server's per-field errors over the client's.
 *
 * Server wins: it is the authority, and its message is the one that describes
 * why the write was actually refused.
 */
export function mergeFieldErrors(
  client: FieldErrors,
  server: Record<string, string[]>,
): FieldErrors {
  const merged = { ...client }

  for (const [field, messages] of Object.entries(server)) {
    const first = messages[0]
    if (first !== undefined) {
      merged[field] = first
    }
  }

  return merged
}
