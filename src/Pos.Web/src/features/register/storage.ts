/**
 * What the register keeps across a reload.
 *
 * **Two things, for two different reasons.** The cart is here so an accidental
 * F5 with twenty items scanned does not make a cashier ring them again. The
 * in-flight record is here for the case that costs money: a reload *during* a
 * payment, where the sale may or may not have been written and the till comes
 * back knowing neither.
 *
 * `sessionStorage`, not `localStorage`, and the same reasoning as
 * `auth/tokenStore.ts`: a shared counter tablet must not hand the next shift the
 * last one's basket. It dies with the tab, which is the correct direction to
 * fail here.
 *
 * **The manager's override grant is deliberately not in this file.** It lives in
 * `OverrideProvider` as React state and nowhere else — a five-minute credential
 * written to a shared device's disk is a different class of thing from a list of
 * SKUs, and `OverrideProvider.test.tsx` fails if one ever reaches storage.
 *
 * Everything read back is treated as untrusted input. It is a string a user
 * could have edited, from a build that may not be this one.
 */

import type { ServerDecimal } from '@/lib/money'
import type { Cart, CartLine } from './cart'
import type { Tender } from './tender'

const CART_KEY = 'pos.register.cart'
const IN_FLIGHT_KEY = 'pos.register.saleInFlight'

/**
 * Bumped when either persisted shape changes.
 *
 * A payload from an older version is **dropped, not migrated**. The cost of
 * being wrong is a cashier re-scanning a basket; the cost of migrating a shape
 * badly is a till that crashes on load and cannot be recovered by reloading,
 * which is the only remedy anyone on a shop floor has.
 */
const VERSION = 1

/** A sale that has been submitted and whose answer has not been seen. */
export interface SaleInFlight {
  /** The `clientTransactionId` and `Idempotency-Key` that was sent. */
  saleKey: string
  tenders: readonly Tender[]
  /** The till that submitted it, so another till does not adopt this sale. */
  registerId: string
  shiftId: string
  /** Epoch ms, for the handoff notes and for a human reading storage. */
  at: number
}

/** The cart as it is written down — `flashedKey` is not part of it. */
type PersistedCart = Omit<Cart, 'flashedKey'>

export function readCart(): Cart | null {
  const stored = read<PersistedCart>(CART_KEY, isPersistedCart)

  if (stored === null) {
    return null
  }

  // A flash is a 400ms animation cue for a scan that happened before the
  // reload. Restoring one would highlight a line for an event that is over.
  return { ...stored, flashedKey: null }
}

export function writeCart(cart: Cart): void {
  const { flashedKey: _flashedKey, ...rest } = cart

  write(CART_KEY, rest)
}

export function clearCart(): void {
  remove(CART_KEY)
}

export function readSaleInFlight(): SaleInFlight | null {
  return read<SaleInFlight>(IN_FLIGHT_KEY, isSaleInFlight)
}

export function writeSaleInFlight(record: SaleInFlight): void {
  write(IN_FLIGHT_KEY, record)
}

export function clearSaleInFlight(): void {
  remove(IN_FLIGHT_KEY)
}

/**
 * Reads, parses and validates — returning `null` for anything it does not fully
 * recognise.
 *
 * **Never throws.** A till whose stored cart is malformed must still open; one
 * that threw here would fail on every load, and reloading is the only thing a
 * cashier can do about it.
 */
function read<T>(key: string, isValid: (value: unknown) => value is T): T | null {
  let raw: string | null

  try {
    raw = sessionStorage.getItem(key)
  } catch {
    // Safari in private mode, and any embedded webview with storage disabled.
    return null
  }

  if (raw === null) {
    return null
  }

  let parsed: unknown

  try {
    parsed = JSON.parse(raw)
  } catch {
    remove(key)
    return null
  }

  if (
    typeof parsed !== 'object' ||
    parsed === null ||
    (parsed as { version?: unknown }).version !== VERSION
  ) {
    remove(key)
    return null
  }

  const { data } = parsed as { data?: unknown }

  if (!isValid(data)) {
    remove(key)
    return null
  }

  return data
}

function write(key: string, data: unknown): void {
  try {
    sessionStorage.setItem(key, JSON.stringify({ version: VERSION, data }))
  } catch {
    // Quota, or storage disabled. A till that cannot persist still sells; it
    // just loses the basket on a reload, which is where this started.
  }
}

function remove(key: string): void {
  try {
    sessionStorage.removeItem(key)
  } catch {
    // Nothing to do, and nothing depends on it having worked.
  }
}

function isPersistedCart(value: unknown): value is PersistedCart {
  if (!isRecord(value)) {
    return false
  }

  return (
    Array.isArray(value['lines']) &&
    value['lines'].every(isCartLine) &&
    isNullableString(value['selectedKey']) &&
    isNullableNumber(value['cartDiscountAmount']) &&
    isNullableString(value['saleKey'])
  )
}

/**
 * The fields the reducer and the request builder actually read.
 *
 * Not an exhaustive schema check — no validator library, and inventing one for
 * this would be a dependency for a file that reads two keys. It is enough that
 * nothing downstream reads a field this did not confirm.
 */
function isCartLine(value: unknown): value is CartLine {
  if (!isRecord(value)) {
    return false
  }

  return (
    typeof value['key'] === 'string' &&
    typeof value['productId'] === 'string' &&
    typeof value['description'] === 'string' &&
    typeof value['sku'] === 'string' &&
    typeof value['unit'] === 'string' &&
    typeof value['quantity'] === 'number' &&
    // `ServerDecimal` is `number | string` — the API sends numbers today, but
    // the type permits both and a validator that insisted on one would drop
    // every stored cart. Silently: a rejected payload looks exactly like a till
    // that had nothing in it, so the feature would appear to work and do
    // nothing.
    isServerDecimal(value['unitPrice']) &&
    typeof value['unitPriceMinor'] === 'number' &&
    isNullableNumber(value['unitPriceOverride']) &&
    isNullableNumber(value['discountAmount'])
  )
}

function isSaleInFlight(value: unknown): value is SaleInFlight {
  if (!isRecord(value)) {
    return false
  }

  return (
    typeof value['saleKey'] === 'string' &&
    typeof value['registerId'] === 'string' &&
    typeof value['shiftId'] === 'string' &&
    typeof value['at'] === 'number' &&
    Array.isArray(value['tenders']) &&
    value['tenders'].every(
      (tender: unknown) =>
        isRecord(tender) &&
        typeof tender['key'] === 'string' &&
        typeof tender['amountMinor'] === 'number',
    )
  )
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function isServerDecimal(value: unknown): value is ServerDecimal {
  return typeof value === 'number' || typeof value === 'string'
}

function isNullableString(value: unknown): value is string | null {
  return value === null || typeof value === 'string'
}

function isNullableNumber(value: unknown): value is number | null {
  return value === null || typeof value === 'number'
}
