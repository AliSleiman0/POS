/**
 * Turning a cart into a sale when there is no server to ask.
 *
 * **This is the one place the client decides what a customer pays**, and it
 * exists because the alternative is a till that stops selling when the shop's
 * line goes down — the single most common reason small businesses reject a POS,
 * per `DECISIONS.md`.
 *
 * The amounts come from `lib/pricing`, which is a port of `Pos.Core.Pricing`
 * pinned to the C# engine by a shared corpus asserted from both sides. Read the
 * header of `pricingEngine.ts` for the invariant-3 amendment; the short version
 * is that there are two implementations and CI fails if they disagree on any of
 * 913 baskets.
 *
 * **What is built here is the request body the server will eventually receive**,
 * not a parallel representation of a sale. The queue replays it byte for byte,
 * so the sale the server writes is priced by the server from the same inputs —
 * the local amounts are what the customer was *shown* and what the receipt says,
 * and the corpus is what makes those the same numbers.
 */

import type { Cart } from '@/features/register/cart'
import { toSaleLines } from '@/features/register/cart'
import type { Tender } from '@/features/register/tender'
import { asMoney, parseDecimal, price, type CartLine as PricedInput } from '@/lib/pricing'
import type { OutboxDisplay } from '@/lib/offline/db'
import type { MirroredSettings, OfflineDb } from '@/lib/offline/db'
import { readTaxClass } from '@/lib/offline/catalog'

/** What the mirror has to supply for a line the engine can price. */
export interface OfflineLineInput {
  productId: string
  description: string
  quantity: string
  unitPrice: string
  taxRate: string
  lineDiscount: string
}

export interface OfflineSale {
  /** The `POST /sales` body, ready to queue and replay unchanged. */
  body: unknown
  /** What the cashier is shown and what the receipt prints. */
  display: OutboxDisplay
  /** ISO-8601, minted once here. Never re-read on a retry. */
  occurredAt: string
}

/**
 * Prices the cart locally and builds the request the server will get.
 *
 * Synchronous and free of the database: the lines arrive already resolved, so
 * this is a pure function of its inputs and can be exercised without IndexedDB.
 *
 * @throws {InvalidDiscountError} for a basket the server would also refuse —
 * a discount larger than the line it applies to, say. Refused *here* rather
 * than discovered as a permanent sync failure hours later, with the money
 * already in the drawer and the customer long gone.
 */
export function buildOfflineSale(input: {
  cart: Cart
  registerId: string
  shiftId: string
  tenders: readonly Tender[]
  settings: MirroredSettings
  /** Already resolved against the mirror by {@link resolveOfflineLines}. */
  lines: OfflineLineInput[]
  now?: Date
}): OfflineSale {
  const saleKey = input.cart.saleKey

  if (saleKey === null) {
    // The same programming error `useCompleteSale` refuses: without a key the
    // retry story is gone and a double-tap is a double charge.
    throw new Error('The sale has no idempotency key — dispatch beginSale first.')
  }

  const priced = price({
    lines: input.lines.map(toPricedLine),
    cartDiscount: asMoney(parseDecimal(input.cart.cartDiscountAmount?.toString() ?? '0')),
    taxMode: input.settings.taxMode,
    cashRoundingIncrement: parseDecimal(input.settings.cashRoundingIncrement),
  })

  /*
   * Minted once, here, and stored with the record.
   *
   * `occurredAt` is part of the request body and therefore part of the
   * fingerprint `IdempotencyFilter` hashes. A retry that re-read the clock
   * would send a different body under the same key, and the server would answer
   * `409 idempotency-key-reused` — for ever, which at a till reads as a sale
   * that simply will not go through.
   */
  const occurredAt = (input.now ?? new Date()).toISOString()

  const tendered = input.tenders.reduce((sum, tender) => sum + tender.amountMinor, 0)

  const body = {
    clientTransactionId: saleKey,
    registerId: input.registerId,
    shiftId: input.shiftId,
    lines: toSaleLines(input.cart),
    cartDiscountAmount: input.cart.cartDiscountAmount,
    tenders: input.tenders.map((tender) => ({
      method: 'Cash' as const,
      // Back to a decimal for the wire, exactly as the online path does. Held
      // as integer minor units until here so nothing accumulated float error.
      amount: tender.amountMinor / 100,
      reference: null,
    })),
    occurredAt,
  }

  const total = Number(moneyText(priced.total))
  const change = Math.max(0, tendered / 100 - total)

  return {
    body,
    occurredAt,
    display: {
      lines: priced.lines.map((line, index) => ({
        description: input.lines[index]!.description,
        quantity: input.lines[index]!.quantity,
        unitPrice: input.lines[index]!.unitPrice,
        lineTotal: moneyText(line.total),
      })),
      subtotal: moneyText(priced.subtotal),
      taxTotal: moneyText(priced.taxTotal),
      discountTotal: moneyText(priced.discountTotal),
      roundingAdjustment: moneyText(priced.roundingAdjustment),
      total: moneyText(priced.total),
      tendered: (tendered / 100).toFixed(2),
      change: change.toFixed(2),
      currencyCode: input.settings.currencyCode,
    },
  }
}

/**
 * Resolves the cart's lines against the mirror.
 *
 * Returns `null` when anything is missing — a product or a tax class the mirror
 * has not got. **Refused rather than guessed**: pricing a line at a rate the
 * till invented would charge a customer tax nobody legislated, and the cashier
 * would have no way to know.
 */
export async function resolveOfflineLines(
  db: OfflineDb,
  cart: Cart,
): Promise<OfflineLineInput[] | null> {
  const resolved: OfflineLineInput[] = []

  for (const line of cart.lines) {
    const product = await db.get('products', line.productId)

    if (product === undefined) {
      return null
    }

    const taxClass = await readTaxClass(db, product.taxClassId)

    if (taxClass === undefined) {
      return null
    }

    resolved.push({
      productId: line.productId,
      description: line.description,
      quantity: String(line.quantity),

      // A manager's override if there is one, otherwise the mirror's price.
      // The same precedence `effectiveUnitPriceMinor` applies for the
      // provisional subtotal, so the two never disagree on screen.
      unitPrice:
        line.unitPriceOverride === null ? product.unitPrice : String(line.unitPriceOverride),
      taxRate: taxClass.rate,
      lineDiscount: line.discountAmount === null ? '0' : String(line.discountAmount),
    })
  }

  return resolved
}

function toPricedLine(line: OfflineLineInput): PricedInput {
  return {
    productId: line.productId,
    description: line.description,
    quantity: parseDecimal(line.quantity),
    unitPrice: asMoney(parseDecimal(line.unitPrice)),
    taxRate: parseDecimal(line.taxRate),
    lineDiscount: asMoney(parseDecimal(line.lineDiscount)),
  }
}

/** A priced amount as its exact decimal literal. Never through a JS number. */
function moneyText(amount: { m: bigint; s: number }): string {
  const negative = amount.m < 0n
  const digits = (negative ? -amount.m : amount.m).toString().padStart(amount.s + 1, '0')

  const whole = digits.slice(0, digits.length - amount.s)
  const fraction = amount.s === 0 ? '' : `.${digits.slice(digits.length - amount.s)}`

  return `${negative ? '-' : ''}${whole}${fraction}`
}
