/**
 * Offline pricing: the client-side half of a rule set the server owns.
 *
 * **Import from here, not from the files underneath.** The narrow surface is
 * the point — `decimal.ts` exposes a general arithmetic that has no business
 * anywhere near a component, and CLAUDE.md invariant 3 still holds everywhere
 * this module is not: money arrives from the server and the client displays it.
 *
 * The one exception this module represents is written up in `DECISIONS.md` and
 * at the top of `pricingEngine.ts`. In short: an offline till has no
 * `POST /sales/quote` to ask and still has to tell a customer what they owe, so
 * a second implementation exists and is pinned to the first by a shared corpus
 * that both test suites assert against.
 *
 * **Consult it only when the server cannot be reached.** `useQuote` remains
 * authoritative online; a component that reached for this while a quote was
 * available would be choosing the less trustworthy of two answers.
 */

export { price } from './pricingEngine'
export type { Cart, CartLine, PricedLine, PricedSale } from './pricingEngine'

export { changeFor, isSufficient, UnderTenderError } from './tenderRules'

export { InvalidDiscountError } from './errors'

export type { TaxMode } from './taxCalculator'

export type { Money } from './money'
export {
  fromServerDecimal as moneyFromServer,
  toString as moneyToString,
  ZERO as ZERO_MONEY,
  money as asMoney,
} from './money'

export { fromServerDecimal as decimalFromServer, parse as parseDecimal } from './decimal'
export type { Dec } from './decimal'
