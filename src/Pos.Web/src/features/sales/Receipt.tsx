import { formatMoney, formatQuantity, formatRate, parseServerDecimal } from '@/lib/money'
import { cn } from '@/lib/utils'
import type { Receipt as ReceiptPayload } from './queries'
import './receipt.css'

/** 80mm thermal roll, or A4 for a shop printing to an office printer. */
export type Paper = '80mm' | 'a4'

/**
 * A receipt, as it prints.
 *
 * **This component decides nothing.** Every amount, the tax breakdown, the
 * timestamp in the tenant's zone and the shop's header block all arrive from
 * `GET /sales/{id}/receipt`, computed once by `ReceiptBuilder`. Anything this
 * file worked out for itself would be a second renderer disagreeing with the
 * thermal printer and the emailed copy — which is the failure §6.1 exists to
 * prevent, and the one a tax authority finds.
 *
 * The same element is the on-screen preview and the printed output. A separate
 * "print view" is how a preview stops matching the paper.
 */
export function Receipt({
  receipt,
  isReprint,
  paper = '80mm',
}: {
  receipt: ReceiptPayload
  /**
   * Whether this copy is a reprint.
   *
   * Decided by the caller, because the server keeps no count of copies — see
   * `DECISIONS.md`. The completion panel prints an original; everything reached
   * from history is a reprint. An unmarked duplicate receipt is a refund-fraud
   * vector, so the default is the safe one.
   */
  isReprint: boolean
  paper?: Paper
}) {
  const currency = receipt.shop.currencyCode

  return (
    <div
      data-testid="receipt"
      data-paper={paper}
      className={cn('receipt-paper', paper === 'a4' && 'receipt-paper--a4')}
    >
      {/* `@page` is document-level and cannot be selected by class, so the page
          box is swapped by swapping the rule. */}
      <style>
        {paper === 'a4'
          ? '@page { size: A4; margin: 12mm; }'
          : '@page { size: 80mm auto; margin: 0; }'}
      </style>

      <Banner receipt={receipt} isReprint={isReprint} />

      <div className="receipt-centre">
        <div className="receipt-strong">{receipt.shop.name}</div>
        {receipt.shop.addressLine !== null ? (
          <div className="receipt-wrap">{receipt.shop.addressLine}</div>
        ) : null}
        {receipt.shop.taxNumber !== null ? <div>VAT {receipt.shop.taxNumber}</div> : null}
        {receipt.shop.header !== null ? (
          <div className="receipt-wrap">{receipt.shop.header}</div>
        ) : null}
      </div>

      <hr className="receipt-rule" />

      <table>
        <tbody>
          <Meta label="Sale" value={`#${String(receipt.saleNumber)}`} />
          <Meta label="Date" value={formatStamp(receipt.completedAtLocal)} />
          <Meta label="Served by" value={receipt.cashierName} />
          <Meta label="Till" value={receipt.registerName} />
          {receipt.originalSaleNumber !== null ? (
            <Meta label="Against sale" value={`#${String(receipt.originalSaleNumber)}`} />
          ) : null}
        </tbody>
      </table>

      <hr className="receipt-rule" />

      <table>
        <tbody>
          {receipt.lines.map((line) => (
            <tr key={line.lineNumber}>
              <td>
                {/* The snapshot, printed as it was rung — never the catalog's
                    current name. Wraps rather than clips: a truncated product
                    name on a receipt is an argument at a counter. */}
                <div className="receipt-wrap">{line.description}</div>
                <div>
                  {formatQuantity(line.quantity)} × {formatMoney(line.unitPrice, currency)}
                  {line.isPriceOverridden ? ' (price changed)' : ''}
                </div>
                {parseServerDecimal(line.discount) !== 0 ? (
                  <div>Discount {formatMoney(line.discount, currency)}</div>
                ) : null}
              </td>
              <td className="receipt-amount">{formatMoney(line.lineTotal, currency)}</td>
            </tr>
          ))}
        </tbody>
      </table>

      <hr className="receipt-rule" />

      <table>
        <tbody>
          {parseServerDecimal(receipt.discountTotal) !== 0 ? (
            <Amount label="Discounts" value={formatMoney(receipt.discountTotal, currency)} />
          ) : null}
          <Amount label="Subtotal" value={formatMoney(receipt.subtotal, currency)} />
          {parseServerDecimal(receipt.roundingAdjustment) !== 0 ? (
            <Amount
              label="Cash rounding"
              value={formatMoney(receipt.roundingAdjustment, currency)}
            />
          ) : null}
          <Amount
            label="Total"
            value={formatMoney(receipt.total, currency)}
            className="receipt-strong"
          />
        </tbody>
      </table>

      <hr className="receipt-rule" />

      {/* Legally required in most jurisdictions and not derivable from a single
          total: a basket of zero-rated bread and standard-rated wine has one
          tax total and two rates behind it. The parts sum to `taxTotal`
          exactly, arranged server-side. */}
      <table data-testid="receipt-tax-breakdown">
        <tbody>
          <tr>
            <td className="receipt-strong">VAT</td>
            <td className="receipt-amount receipt-strong">Net</td>
            <td className="receipt-amount receipt-strong">Tax</td>
          </tr>
          {receipt.taxBreakdown.map((part) => (
            <tr key={String(part.rate)}>
              <td>{formatRate(part.rate)}</td>
              <td className="receipt-amount">{formatMoney(part.netAmount, currency)}</td>
              <td className="receipt-amount">{formatMoney(part.taxAmount, currency)}</td>
            </tr>
          ))}
        </tbody>
      </table>

      <hr className="receipt-rule" />

      <table>
        <tbody>
          {receipt.tenders.map((tender, index) => (
            <Amount
              key={index}
              label={tender.method}
              value={formatMoney(tender.amount, currency)}
            />
          ))}
          {parseServerDecimal(receipt.changeGiven) !== 0 ? (
            <Amount label="Change" value={formatMoney(receipt.changeGiven, currency)} />
          ) : null}
        </tbody>
      </table>

      {receipt.shop.footer !== null ? (
        <>
          <hr className="receipt-rule" />
          <div className="receipt-centre receipt-wrap">{receipt.shop.footer}</div>
        </>
      ) : null}
    </div>
  )
}

/**
 * What this piece of paper is, when it is not an ordinary sale.
 *
 * Boxed and at the top, because all three of these are facts somebody must not
 * be able to miss: a refund is not a purchase, a voided sale is not proof of
 * one, and a duplicate presented as an original is how a refund is claimed
 * twice.
 */
function Banner({ receipt, isReprint }: { receipt: ReceiptPayload; isReprint: boolean }) {
  return (
    <>
      {receipt.kind === 'VoidedSale' ? (
        <div className="receipt-banner" data-testid="receipt-voided">
          VOIDED — NOT A VALID SALE
          {receipt.voidReason !== null ? <div>{receipt.voidReason}</div> : null}
        </div>
      ) : receipt.kind === 'Refund' ? (
        <div className="receipt-banner" data-testid="receipt-refund">
          REFUND
          {receipt.refundReason !== null ? <div>{receipt.refundReason}</div> : null}
        </div>
      ) : null}

      {isReprint ? (
        <div className="receipt-banner" data-testid="receipt-reprint">
          REPRINT
          <div>{formatStamp(receipt.issuedAtLocal)}</div>
        </div>
      ) : null}
    </>
  )
}

function Meta({ label, value }: { label: string; value: string }) {
  return (
    <tr>
      <td>{label}</td>
      <td className="receipt-amount">{value}</td>
    </tr>
  )
}

function Amount({
  label,
  value,
  className,
}: {
  label: string
  value: string
  className?: string
}) {
  return (
    <tr className={className}>
      <td>{label}</td>
      <td className="receipt-amount">{value}</td>
    </tr>
  )
}

/**
 * A timestamp as it prints.
 *
 * **The offset is dropped deliberately, not forgotten.** The server has already
 * converted the instant into the tenant's zone and the string carries that
 * offset, so the wall-clock digits are the shop's own local time. Re-rendering
 * them through the browser's locale would convert a second time — a receipt
 * printed on a tablet whose clock is set to another country would then show a
 * different time from the one on the Z-report, which is the sort of discrepancy
 * that ends up being blamed on the till.
 */
function formatStamp(value: string): string {
  const match = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})/.exec(value)

  if (match === null) {
    // Not a shape this can read. Printing the raw value is ugly and honest;
    // printing nothing would take the date off a legal document.
    return value
  }

  const [, year, month, day, hour, minute] = match

  return `${day}/${month}/${year} ${hour}:${minute}`
}
