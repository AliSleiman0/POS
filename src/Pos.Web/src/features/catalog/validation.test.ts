import { describe, expect, it } from 'vitest'
import {
  isStorableAmount,
  isValidTaxRate,
  mergeFieldErrors,
  validateBarcode,
  validateProduct,
  validateTaxClass,
  type ProductFormValues,
} from './validation'

const VALID: ProductFormValues = {
  sku: 'APPLE-01',
  name: 'Braeburn apples',
  description: '',
  categoryId: '',
  taxClassId: '11111111-1111-1111-1111-111111111111',
  unitPrice: '1.99',
  costPrice: '',
  unit: 'Each',
  trackStock: true,
}

describe('isStorableAmount', () => {
  it('accepts what numeric(19,4) holds exactly', () => {
    expect(isStorableAmount('0')).toBe(true)
    expect(isStorableAmount('1.99')).toBe(true)
    expect(isStorableAmount('12.9999')).toBe(true)
  })

  it('rejects a fifth decimal place, which the column would silently truncate', () => {
    expect(isStorableAmount('12.99999')).toBe(false)
  })

  it('rejects a negative price', () => {
    // Prices are non-negative — CatalogRules.IsStorableAmount. Movement
    // quantities are the signed case, and they are a different rule.
    expect(isStorableAmount('-1')).toBe(false)
  })

  it('rejects what a parsed number would have accepted', () => {
    // Checked as a string precisely so these do not slip through: parseFloat
    // takes all of them, and 1e3 as a price is a thousand pounds.
    expect(isStorableAmount('1e3')).toBe(false)
    expect(isStorableAmount('0x10')).toBe(false)
    expect(isStorableAmount('.5')).toBe(false)
    expect(isStorableAmount('1.')).toBe(false)
    expect(isStorableAmount('')).toBe(false)
    expect(isStorableAmount('abc')).toBe(false)
  })

  it('trims, because a pasted price often carries a space', () => {
    expect(isStorableAmount(' 1.99 ')).toBe(true)
  })
})

describe('isValidTaxRate', () => {
  it('accepts a fraction', () => {
    expect(isValidTaxRate('0')).toBe(true)
    expect(isValidTaxRate('0.2')).toBe(true)
    // The degenerate but legal 100%.
    expect(isValidTaxRate('1')).toBe(true)
  })

  it('rejects a percentage typed as a whole number', () => {
    // The mistake this exists for. 20 is four hundred times 0.2, and nothing
    // downstream could tell it was meant as a percentage — ck_tax_class_rate_range
    // refuses it, and this says so before the round trip.
    expect(isValidTaxRate('20')).toBe(false)
    expect(isValidTaxRate('1.5')).toBe(false)
  })

  it('rejects a fifth decimal, which numeric(6,4) would truncate', () => {
    expect(isValidTaxRate('0.20005')).toBe(false)
  })
})

describe('validateProduct', () => {
  it('accepts a well-formed product', () => {
    expect(validateProduct(VALID)).toEqual({})
  })

  it('reports every problem at once rather than one at a time', () => {
    // The same contract ProductEndpoints.ValidateAsync honours: a form wrong in
    // three places is told about all three, not corrected three times over.
    const errors = validateProduct({ ...VALID, sku: '', name: '', unitPrice: 'free' })

    expect(Object.keys(errors).sort()).toEqual(['name', 'sku', 'unitPrice'])
  })

  it('requires a tax class, because a product without one cannot be priced', () => {
    expect(validateProduct({ ...VALID, taxClassId: '' })['taxClassId']).toBeDefined()
  })

  it('requires a price, and does not read a blank as free', () => {
    // The reason every field on CreateProductRequest is nullable server-side: a
    // non-nullable decimal binds an omitted field to 0, which is a valid price.
    expect(validateProduct({ ...VALID, unitPrice: '' })['unitPrice']).toBeDefined()
    expect(validateProduct({ ...VALID, unitPrice: '0' })['unitPrice']).toBeUndefined()
  })

  it('validates a cost price that will be dropped anyway', () => {
    // A Manager's cost price is ignored by the server rather than refused. It
    // is still worth telling them the number was malformed, rather than
    // silently discarding it and leaving them to think it saved.
    expect(validateProduct({ ...VALID, costPrice: 'lots' })['costPrice']).toBeDefined()
    expect(validateProduct({ ...VALID, costPrice: '' })['costPrice']).toBeUndefined()
  })

  it('enforces the SKU length before the server has to', () => {
    expect(validateProduct({ ...VALID, sku: 'X'.repeat(65) })['sku']).toBeDefined()
    expect(validateProduct({ ...VALID, sku: 'X'.repeat(64) })['sku']).toBeUndefined()
  })

  it('treats whitespace as absent', () => {
    expect(validateProduct({ ...VALID, name: '   ' })['name']).toBeDefined()
  })

  it('rejects the string "undefined", which is how an omitted field arrives', () => {
    // A real bug, caught by the Playwright suite. `costPrice` is *omitted* from
    // the response for a caller without CanViewMargins (invariant 7) rather
    // than sent as null, so the form's seeding read `undefined` and
    // `String(undefined)` put the six letters "undefined" in the field. Saving
    // then failed on a form nobody had typed in.
    //
    // The form no longer produces it. This pins the other half: if it ever does
    // again, the validator refuses rather than sending it to the server.
    expect(validateProduct({ ...VALID, costPrice: 'undefined' })['costPrice']).toBeDefined()
    expect(validateProduct({ ...VALID, unitPrice: 'undefined' })['unitPrice']).toBeDefined()
    expect(isStorableAmount('undefined')).toBe(false)
  })
})

describe('validateBarcode', () => {
  it('accepts a code and rejects a blank or overlong one', () => {
    expect(validateBarcode('5012345678900')).toBeUndefined()
    expect(validateBarcode('')).toBeDefined()
    expect(validateBarcode('9'.repeat(65))).toBeDefined()
  })
})

describe('validateTaxClass', () => {
  it('accepts a name and a fraction', () => {
    expect(validateTaxClass('Standard', '0.2')).toEqual({})
  })

  it('explains the fraction rather than just refusing', () => {
    expect(validateTaxClass('Standard', '20')['rate']).toContain('20% is 0.2')
  })
})

describe('mergeFieldErrors', () => {
  it('lets the server win, because it is the authority', () => {
    const merged = mergeFieldErrors(
      { sku: 'A SKU is required.' },
      { sku: ["The SKU 'APPLE-01' is already used by another product."] },
    )

    expect(merged['sku']).toContain('already used')
  })

  it('keeps client errors the server did not mention', () => {
    const merged = mergeFieldErrors({ name: 'A name is required.' }, { sku: ['Nope.'] })

    expect(merged).toEqual({ name: 'A name is required.', sku: 'Nope.' })
  })

  it('ignores an empty message list rather than blanking the field', () => {
    expect(mergeFieldErrors({ sku: 'A SKU is required.' }, { sku: [] })['sku']).toBe(
      'A SKU is required.',
    )
  })
})
