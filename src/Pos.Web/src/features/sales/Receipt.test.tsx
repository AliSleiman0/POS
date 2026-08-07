import { render, screen, within } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import type { Receipt as ReceiptPayload } from './queries'
import { Receipt } from './Receipt'

/**
 * The paper.
 *
 * jsdom does not paint, so nothing here can claim the layout is right at 80mm —
 * that is what a person with print emulation open is for, and it happened. What
 * these do pin is the part that is a correctness question rather than a visual
 * one: that the reprint mark appears when and only when it should, that a long
 * product name survives intact into the DOM, and that the component renders the
 * server's tax breakdown rather than working one out.
 */
describe('Receipt', () => {
  it('shows no reprint mark on an original', () => {
    render(<Receipt receipt={payload()} isReprint={false} />)

    expect(screen.queryByTestId('receipt-reprint')).toBeNull()
  })

  it('marks a reprint, with the moment the copy was taken', () => {
    // An unmarked duplicate receipt is a refund-fraud vector — §6.2 — so this is
    // the assertion that matters most in the file.
    render(<Receipt receipt={payload()} isReprint />)

    const mark = screen.getByTestId('receipt-reprint')

    expect(mark).toHaveTextContent('REPRINT')
    expect(mark).toHaveTextContent('08/08/2026 09:30')
  })

  it('prints a long product name in full rather than truncating it', () => {
    // A clipped name on a receipt is a dispute nobody can settle. The wrapping
    // itself is CSS and invisible to jsdom; what is checked here is that the
    // whole string reaches the DOM, which is the half that can regress in TS.
    const name =
      'Organic Free-Range Extra-Mature Farmhouse Cheddar Truckle 2.5kg (Christmas Edition)'

    render(<Receipt receipt={payload({ description: name })} isReprint={false} />)

    expect(screen.getByText(name)).toBeInTheDocument()
  })

  it('renders one row per tax rate, from the server', () => {
    render(<Receipt receipt={payload()} isReprint={false} />)

    const breakdown = screen.getByTestId('receipt-tax-breakdown')

    // Two rates plus the header row. The amounts are the server's; nothing here
    // sums or splits anything.
    expect(within(breakdown).getAllByRole('row')).toHaveLength(3)
    expect(within(breakdown).getByText('0%')).toBeInTheDocument()
    expect(within(breakdown).getByText('23%')).toBeInTheDocument()
  })

  it('says a refund is a refund, and against which sale', () => {
    render(
      <Receipt
        receipt={payload({ kind: 'Refund', refundReason: 'Faulty', originalSaleNumber: 41 })}
        isReprint={false}
      />,
    )

    expect(screen.getByTestId('receipt-refund')).toHaveTextContent('REFUND')
    expect(screen.getByTestId('receipt-refund')).toHaveTextContent('Faulty')
    expect(screen.getByText('#41')).toBeInTheDocument()
  })

  it('says a voided sale is not a valid one', () => {
    render(
      <Receipt
        receipt={payload({ kind: 'VoidedSale', voidReason: 'Rung up twice' })}
        isReprint={false}
      />,
    )

    expect(screen.getByTestId('receipt-voided')).toHaveTextContent('NOT A VALID SALE')
  })

  it('prints the local wall clock the server sent, without converting it again', () => {
    // The server has already put this in the tenant's zone. Re-rendering it
    // through the browser's locale would convert a second time, so a tablet
    // whose clock is set to another country would print a different time from
    // the one on the Z-report.
    render(<Receipt receipt={payload()} isReprint={false} />)

    expect(screen.getByText('07/08/2026 22:15')).toBeInTheDocument()
  })

  it('omits the shop lines a tenant has not filled in', () => {
    render(
      <Receipt
        receipt={payload({ addressLine: null, taxNumber: null, header: null, footer: null })}
        isReprint={false}
      />,
    )

    expect(screen.getByText('Corner Shop')).toBeInTheDocument()
    expect(screen.queryByText(/VAT IE/)).toBeNull()
  })

  it('switches the page box for the A4 fallback', () => {
    const { rerender } = render(<Receipt receipt={payload()} isReprint={false} />)

    expect(screen.getByTestId('receipt')).toHaveAttribute('data-paper', '80mm')

    rerender(<Receipt receipt={payload()} isReprint={false} paper="a4" />)

    expect(screen.getByTestId('receipt')).toHaveAttribute('data-paper', 'a4')
  })

  it('handles a saleNumber the client types as a string', () => {
    // int64 comes through the generated client as `number | string`, the same
    // trap `ServerDecimal` is. A component that assumed one of them would render
    // "NaN" against a perfectly valid response.
    render(<Receipt receipt={payload({ saleNumber: '9007199254740993' })} isReprint={false} />)

    expect(screen.getByText('#9007199254740993')).toBeInTheDocument()
  })
})

/**
 * A receipt as the server sends one, with the pieces a test wants to vary.
 */
function payload(
  overrides: Partial<ReceiptPayload> &
    Partial<ReceiptPayload['shop']> & { description?: string } = {},
): ReceiptPayload {
  const { description, addressLine, taxNumber, header, footer, ...rest } = overrides

  return {
    kind: 'Sale',
    shop: {
      name: 'Corner Shop',
      addressLine: addressLine === undefined ? '14 Harbour Road' : addressLine,
      taxNumber: taxNumber === undefined ? 'IE1234567FA' : taxNumber,
      header: header === undefined ? 'Open 7 days' : header,
      footer: footer === undefined ? 'Thank you' : footer,
      currencyCode: 'EUR',
    },
    saleId: '00000000-0000-0000-0000-0000000000aa',
    saleNumber: 42,
    completedAtLocal: '2026-08-07T22:15:00+01:00',
    issuedAtLocal: '2026-08-08T09:30:00+01:00',
    timeZoneId: 'Europe/Dublin',
    cashierName: 'Ada Byrne',
    registerName: 'Front Counter',
    taxMode: 'Exclusive',
    originalSaleId: null,
    originalSaleNumber: null,
    voidReason: null,
    refundReason: null,
    lines: [
      {
        lineNumber: 1,
        description: description ?? 'Still Water 500ml',
        quantity: 2,
        unitPrice: 1.2,
        taxRate: 0.23,
        discount: 0,
        lineTotal: 2.95,
        isPriceOverridden: false,
      },
      {
        lineNumber: 2,
        description: 'Carrier Bag',
        quantity: 1,
        unitPrice: 0.165,
        taxRate: 0,
        discount: 0,
        lineTotal: 0.17,
        isPriceOverridden: false,
      },
    ],
    taxBreakdown: [
      { rate: 0, netAmount: 0.17, taxAmount: 0, grossAmount: 0.17 },
      { rate: 0.23, netAmount: 2.4, taxAmount: 0.55, grossAmount: 2.95 },
    ],
    tenders: [{ method: 'Cash', amount: 5, changeGiven: 1.88 }],
    subtotal: 2.57,
    discountTotal: 0,
    taxTotal: 0.55,
    roundingAdjustment: 0,
    total: 3.12,
    changeGiven: 1.88,
    ...rest,
  }
}
