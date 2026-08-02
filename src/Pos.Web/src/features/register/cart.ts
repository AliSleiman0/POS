/**
 * The cart, as pure data.
 *
 * **This holds no money.** Every amount a customer sees comes from
 * `POST /sales/quote` and, at the end, from `POST /sales` — a second pricing
 * implementation in TypeScript would disagree with the C# one over rounding,
 * inclusive-tax extraction or discount apportionment, and the disagreement
 * surfaces as a customer being charged something the receipt does not say
 * (CLAUDE.md invariant 3). What lives here is *what the cashier chose*:
 * products, quantities, and which line the keyboard is pointing at.
 *
 * The one price it does carry is `unitPriceMinor`, an integer, used only for the
 * provisional subtotal shown while the quote is in flight.
 *
 * No React: a reducer that can be exercised as a function is one the tests can
 * drive through a hundred scans without rendering anything.
 */

import type { components } from '@/api/schema'
import { toMinorUnits, type MinorUnits, type ServerDecimal } from '@/lib/money'

type Unit = components['schemas']['Unit']
type SaleLineRequest = components['schemas']['SaleLineRequest']

/** What the register needs to know about a product to put it in a cart. */
export interface CartProduct {
  productId: string
  name: string
  sku: string
  unit: Unit
  unitPrice: ServerDecimal
}

export interface CartLine {
  /** Stable across re-renders and re-orders; never sent to the server. */
  key: string
  productId: string
  description: string
  sku: string
  unit: Unit
  quantity: number
  /** Display only, straight from the catalog. */
  unitPrice: ServerDecimal
  /** Integer minor units, for the provisional subtotal only. */
  unitPriceMinor: MinorUnits
}

export interface Cart {
  lines: readonly CartLine[]
  /**
   * The line the keyboard acts on.
   *
   * Part of the cart rather than component state because the whole point of
   * where this lives is that it survives a re-render, a route change and a
   * re-auth overlay — and a selection that reset while the cart did not would
   * send the next keypress to the wrong line.
   */
  selectedKey: string | null
  /**
   * The line the last scan touched, for the flash. Cleared by `clearFlash` once
   * the animation has been shown.
   */
  flashedKey: string | null
}

export const EMPTY_CART: Cart = { lines: [], selectedKey: null, flashedKey: null }

export type CartAction =
  | { type: 'add'; product: CartProduct; quantity?: number }
  | { type: 'setQuantity'; key: string; quantity: number }
  | { type: 'adjustQuantity'; key: string; delta: number }
  | { type: 'select'; key: string | null }
  | { type: 'move'; delta: 1 | -1 }
  | { type: 'remove'; key: string }
  | { type: 'clearFlash' }
  | { type: 'clear' }

/**
 * The largest quantity one line may carry.
 *
 * Not a server rule — it is a fat-finger rule. A keypad entry of `1000000`
 * against a scanned item is a mistake every time, and catching it here is
 * cheaper than a void.
 */
export const MAX_LINE_QUANTITY = 9999

export function cartReducer(state: Cart, action: CartAction): Cart {
  switch (action.type) {
    case 'add': {
      const quantity = action.quantity ?? 1
      const existing = state.lines.find((line) => line.productId === action.product.productId)

      // Scanning the same item twice is two units of one line, not two lines.
      // A till that grew a new line per scan is unreadable by the third beep.
      if (existing !== undefined) {
        return {
          ...state,
          lines: state.lines.map((line) =>
            line.key === existing.key
              ? { ...line, quantity: clampQuantity(line.quantity + quantity) }
              : line,
          ),
          selectedKey: existing.key,
          flashedKey: existing.key,
        }
      }

      const line: CartLine = {
        key: crypto.randomUUID(),
        productId: action.product.productId,
        description: action.product.name,
        sku: action.product.sku,
        unit: action.product.unit,
        quantity: clampQuantity(quantity),
        unitPrice: action.product.unitPrice,
        unitPriceMinor: toMinorUnits(action.product.unitPrice),
      }

      return {
        ...state,
        lines: [...state.lines, line],
        selectedKey: line.key,
        flashedKey: line.key,
      }
    }

    case 'setQuantity': {
      const quantity = clampQuantity(action.quantity)

      // Zero is a removal expressed differently, and a till that left an empty
      // line on the screen would sell it as one.
      if (quantity <= 0) {
        return cartReducer(state, { type: 'remove', key: action.key })
      }

      return {
        ...state,
        lines: state.lines.map((line) => (line.key === action.key ? { ...line, quantity } : line)),
      }
    }

    case 'adjustQuantity': {
      const line = state.lines.find((candidate) => candidate.key === action.key)

      if (line === undefined) {
        return state
      }

      return cartReducer(state, {
        type: 'setQuantity',
        key: action.key,
        // Rounded because `0.1 + 0.2` is not `0.3`, and a weighed line stepped
        // twice must not become 0.30000000000000004 kg on the wire.
        quantity: roundQuantity(line.quantity + action.delta),
      })
    }

    case 'select':
      return { ...state, selectedKey: action.key }

    case 'move': {
      if (state.lines.length === 0) {
        return state
      }

      const current = state.lines.findIndex((line) => line.key === state.selectedKey)
      // From nothing selected, ↓ takes the first line and ↑ the last.
      const next =
        current === -1
          ? action.delta === 1
            ? 0
            : state.lines.length - 1
          : clampIndex(current + action.delta, state.lines.length)

      return { ...state, selectedKey: state.lines[next]?.key ?? null }
    }

    case 'remove': {
      const lines = state.lines.filter((line) => line.key !== action.key)

      if (lines.length === state.lines.length) {
        return state
      }

      // Selection moves to the line that took its place, so voiding three lines
      // in a row is three presses of the same key rather than a re-aim each time.
      const removedAt = state.lines.findIndex((line) => line.key === action.key)
      const selected =
        state.selectedKey === action.key
          ? (lines[Math.min(removedAt, lines.length - 1)]?.key ?? null)
          : state.selectedKey

      return {
        ...state,
        lines,
        selectedKey: selected,
        flashedKey: state.flashedKey === action.key ? null : state.flashedKey,
      }
    }

    case 'clearFlash':
      return state.flashedKey === null ? state : { ...state, flashedKey: null }

    case 'clear':
      return EMPTY_CART
  }
}

/** Whether there is anything to price, tender or void. */
export function isEmpty(cart: Cart): boolean {
  return cart.lines.length === 0
}

/**
 * The cart as the API's `lines`.
 *
 * **The one place a cart becomes a request.** `POST /sales/quote` and
 * `POST /sales` both go through here, so the thing that was priced and the thing
 * that is sold cannot be different — which is the failure that shows up as a
 * total changing between the display and the receipt.
 *
 * `unitPriceOverride` and `discountAmount` are `null` until 5.3 builds the
 * controls for them; both are permission-gated server-side and a Cashier sending
 * one is refused rather than ignored.
 */
export function toSaleLines(cart: Cart): SaleLineRequest[] {
  return cart.lines.map((line) => ({
    productId: line.productId,
    quantity: line.quantity,
    unitPriceOverride: null,
    discountAmount: null,
  }))
}

/**
 * A stable identity for what would be quoted.
 *
 * The quote's query key, so a cart that changes and changes back reuses the
 * cached price instead of another round trip, and re-selecting a line — which
 * changes the cart object but not the money — does not re-quote at all.
 */
export function cartSignature(cart: Cart): string {
  return cart.lines.map((line) => `${line.productId}:${String(line.quantity)}`).join('|')
}

/** Provisional line totals in integer minor units. Never authoritative. */
export function provisionalLineMinor(line: CartLine): MinorUnits {
  return Math.round(line.unitPriceMinor * line.quantity)
}

function clampIndex(index: number, length: number): number {
  return Math.max(0, Math.min(index, length - 1))
}

function clampQuantity(quantity: number): number {
  return Math.min(roundQuantity(quantity), MAX_LINE_QUANTITY)
}

/**
 * Four decimal places, matching `numeric(19,4)` and `CatalogRules.IsStorableAmount`.
 *
 * A quantity the server would reject is one the cashier cannot see the reason
 * for, so it never gets sent: 0.350 kg of cheese is ordinary, 0.35000000000004
 * is a rounding artefact of having added 0.1 to 0.25.
 */
function roundQuantity(quantity: number): number {
  return Math.round(quantity * 10_000) / 10_000
}
