/**
 * The cart's total when there is no `POST /sales/quote` to ask for it.
 *
 * **Without this the offline till can scan and cannot sell**, which is the
 * failure the whole phase exists to remove: the tender step is gated on the
 * server's quote having returned, so a cashier offline would fill a basket and
 * find Take Cash greyed out with a customer standing there.
 *
 * It returns the **same shape** the server's quote does, so `TotalPanel` and
 * `TenderPanel` need no knowledge of where the number came from. That is
 * deliberate: a component that branched on the source would eventually format
 * one of them differently, and the two numbers a customer sees — the total read
 * out and the total on the receipt — must be produced by one path.
 *
 * Only consulted while the connectivity probe says offline. Online, the
 * server's quote remains authoritative, exactly as invariant 3 requires.
 */

import { useEffect, useState } from 'react'
import type { components } from '@/api/schema'
import { isEmpty, type Cart } from '@/features/register/cart'
import { readSettings } from '@/lib/offline/catalog'
import type { OfflineDb } from '@/lib/offline/db'
import { moneyToString, price } from '@/lib/pricing'
import { resolveOfflineLines, type OfflineLineInput } from './offlineSale'
import { asMoney, parseDecimal } from '@/lib/pricing'

type SaleResponse = components['schemas']['SaleResponse']

export interface OfflineQuote {
  /** Shaped like the server's, so the panels cannot tell the difference. */
  quote: SaleResponse | undefined
  /** The mirror could not price this basket, and the register must say so. */
  unpriceable: boolean
  /** The resolved lines, reused by the sale so nothing is looked up twice. */
  lines: OfflineLineInput[] | null
}

export function useOfflineQuote(db: OfflineDb | null, cart: Cart, enabled: boolean): OfflineQuote {
  const [state, setState] = useState<OfflineQuote>({
    quote: undefined,
    unpriceable: false,
    lines: null,
  })

  useEffect(() => {
    if (!enabled || db === null || isEmpty(cart)) {
      setState({ quote: undefined, unpriceable: false, lines: null })
      return
    }

    let cancelled = false

    void (async () => {
      const settings = await readSettings(db)
      const lines = await resolveOfflineLines(db, cart)

      if (cancelled) {
        return
      }

      /*
       * The mirror is missing a product or a tax class.
       *
       * Reported rather than guessed. Pricing a line at an invented rate would
       * charge a customer tax nobody legislated, and neither they nor the
       * cashier would ever know — so the register shows the basket and refuses
       * the tender step, which is the honest failure.
       */
      if (settings === null || lines === null) {
        setState({ quote: undefined, unpriceable: true, lines: null })
        return
      }

      try {
        const priced = price({
          lines: lines.map((line) => ({
            productId: line.productId,
            description: line.description,
            quantity: parseDecimal(line.quantity),
            unitPrice: asMoney(parseDecimal(line.unitPrice)),
            taxRate: parseDecimal(line.taxRate),
            lineDiscount: asMoney(parseDecimal(line.lineDiscount)),
          })),
          cartDiscount: asMoney(parseDecimal(String(cart.cartDiscountAmount ?? 0))),
          taxMode: settings.taxMode,
          cashRoundingIncrement: parseDecimal(settings.cashRoundingIncrement),
        })

        if (cancelled) {
          return
        }

        setState({
          quote: {
            id: null,
            saleNumber: null,
            type: 'Sale',
            status: null,
            subtotal: moneyToString(priced.subtotal),
            discountTotal: moneyToString(priced.discountTotal),
            taxTotal: moneyToString(priced.taxTotal),
            roundingAdjustment: moneyToString(priced.roundingAdjustment),
            total: moneyToString(priced.total),
            changeGiven: '0',
            completedAt: null,
            lines: priced.lines.map((line, index) => ({
              id: '00000000-0000-0000-0000-000000000000',
              productId: lines[index]!.productId,
              lineNumber: line.lineNumber,
              description: lines[index]!.description,
              quantity: lines[index]!.quantity,
              unitPrice: lines[index]!.unitPrice,
              taxRate: lines[index]!.taxRate,
              discountAmount: moneyToString(line.discount),
              lineSubtotal: moneyToString(line.subtotal),
              lineTax: moneyToString(line.tax),
              lineTotal: moneyToString(line.total),
              isPriceOverridden: false,
            })),
            tenders: [],

            // Zero, because an offline till cannot take one: a tip is keyed on the
            // bill screen, and a bill is server-side state a queued sale has no
            // access to. Stated rather than omitted, so the cast stays honest
            // about the shape it is claiming.
            tipAmount: '0',
          } as SaleResponse,
          unpriceable: false,
          lines,
        })
      } catch {
        /*
         * A basket the engine refuses — a discount larger than the line it
         * applies to. The server would refuse it too, so refusing here is the
         * same answer arriving sooner, before the money is in the drawer.
         */
        if (!cancelled) {
          setState({ quote: undefined, unpriceable: true, lines: null })
        }
      }
    })()

    return () => {
      cancelled = true
    }
  }, [cart, db, enabled])

  return state
}
