import { render, screen, within } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { ReportView } from './ReportView'
import type { Report } from './queries'

/**
 * The report screen.
 *
 * The arithmetic is the server's and is tested there. What is pinned here is the
 * part a reader could be misled by: that a drawer nobody has counted is labelled
 * as such rather than shown with a variance, that a shortage is not styled like a
 * surplus, and that voids and refunds carry the actor and the reason that make
 * them worth listing at all.
 */
describe('ReportView', () => {
  it('shows the headline chain from the server, unchanged', () => {
    render(<ReportView report={report()} />)

    expect(screen.getByTestId('report-total')).toHaveTextContent('€120.00')
    expect(screen.getByText('Average basket').nextSibling).toHaveTextContent('€40.00')
  })

  it('labels an open drawer as an expectation and shows no variance', () => {
    // The distinction that matters most on this screen. A provisional figure
    // read as a reconciled one is a variance nobody has measured.
    render(<ReportView report={report({ isProvisional: true, counted: null, variance: null })} />)

    expect(screen.getByTestId('report-provisional')).toBeInTheDocument()
    expect(screen.getByText('Not counted')).toBeInTheDocument()
    expect(screen.queryByTestId('report-variance')).toBeNull()
  })

  it('shows a counted drawer with its stored variance and no provisional notice', () => {
    render(<ReportView report={report()} />)

    expect(screen.queryByTestId('report-provisional')).toBeNull()
    expect(screen.getByTestId('report-variance')).toHaveTextContent('-€5.00')
  })

  it('marks a short drawer differently from one that balances', () => {
    // Negative means short, and short is the direction somebody has to explain.
    const { rerender } = render(<ReportView report={report({ variance: -5 })} />)

    expect(screen.getByTestId('report-variance')).toHaveClass('text-destructive')

    rerender(<ReportView report={report({ variance: 5 })} />)

    expect(screen.getByTestId('report-variance')).not.toHaveClass('text-destructive')
  })

  it('lists each void with who did it and why', () => {
    render(<ReportView report={report()} />)

    const listed = screen.getByTestId('report-voids')

    expect(within(listed).getByText('#41')).toBeInTheDocument()
    expect(within(listed).getByText('Rung up twice')).toBeInTheDocument()
    expect(within(listed).getByText('Ada Byrne')).toBeInTheDocument()
  })

  it('says so plainly when nothing was voided or refunded', () => {
    // An empty section with no words in it reads as a screen that failed to
    // load, which is the wrong thing to conclude from a clean day.
    render(<ReportView report={report({ voids: [], refunds: [] })} />)

    expect(screen.getByText('No sales were voided.')).toBeInTheDocument()
    expect(screen.getByText('Nothing was refunded.')).toBeInTheDocument()
  })

  it('renders the tax rows the server sent without adding them up itself', () => {
    render(<ReportView report={report()} />)

    const rows = within(screen.getByTestId('report-tax-by-rate')).getAllByRole('row')

    // Header plus two rates.
    expect(rows).toHaveLength(3)
    expect(screen.getByText('23%')).toBeInTheDocument()
  })

  it('handles counts the client types as strings', () => {
    // int32 comes through the generated client as `number | string`, the same
    // union as every amount. A `> 0` against the raw value would not compile and
    // a bare Number() would accept rubbish.
    render(<ReportView report={report({ refundCount: '2', refundTotal: '-9.99' })} />)

    expect(screen.getByText('Refunds (2)')).toBeInTheDocument()
  })
})

function report(
  overrides: Partial<Report['cash']> & Partial<Report['sales']> & Partial<Report> = {},
): Report {
  const { isProvisional, counted, variance, refundCount, refundTotal, ...rest } = overrides

  return {
    scope: {
      kind: 'Shift',
      shiftId: '00000000-0000-0000-0000-0000000000bb',
      date: null,
      fromUtc: '2026-08-07T07:00:00+00:00',
      toUtc: '2026-08-07T17:00:00+00:00',
      timeZoneId: 'Europe/Dublin',
    },
    currencyCode: 'EUR',
    sales: {
      transactionCount: 3,
      gross: 100,
      discounts: 5,
      net: 95,
      tax: 25,
      rounding: 0,
      total: 120,
      refundTotal: refundTotal ?? 0,
      refundCount: refundCount ?? 0,
      averageBasket: 40,
    },
    taxByRate: [
      { rate: 0, net: 10, tax: 0 },
      { rate: 0.23, net: 85, tax: 25 },
    ],
    tenders: [{ method: 'Cash', amount: 200, changeGiven: 80, net: 120 }],
    cash: {
      openingFloat: 100,
      cashSales: 120,
      cashRefunds: 0,
      cashMovements: -50,
      expected: 170,
      counted: counted === undefined ? 165 : counted,
      variance: variance === undefined ? -5 : variance,
      isProvisional: isProvisional ?? false,
      movements: [
        {
          id: '00000000-0000-0000-0000-0000000000cc',
          type: 'Drop',
          amount: -50,
          reason: 'To the safe',
          performedBy: 'Ada Byrne',
          occurredAt: '2026-08-07T12:00:00+00:00',
        },
      ],
    },
    shifts: [
      {
        id: '00000000-0000-0000-0000-0000000000bb',
        registerName: 'Front Counter',
        status: 'Closed',
        openedBy: 'Ada Byrne',
        openedAt: '2026-08-07T07:00:00+00:00',
        closedBy: 'Ada Byrne',
        closedAt: '2026-08-07T17:00:00+00:00',
        openingFloat: 100,
        expectedCash: 170,
        countedCash: 165,
        variance: -5,
      },
    ],
    voids: [
      {
        saleId: '00000000-0000-0000-0000-0000000000dd',
        saleNumber: 41,
        total: 6.15,
        at: '2026-08-07T11:00:00+00:00',
        actor: 'Ada Byrne',
        reason: 'Rung up twice',
        originalSaleNumber: null,
      },
    ],
    refunds: [
      {
        saleId: '00000000-0000-0000-0000-0000000000ee',
        saleNumber: 44,
        total: -12.3,
        at: '2026-08-07T13:00:00+00:00',
        actor: 'Sam Cole',
        reason: 'Faulty',
        originalSaleNumber: 42,
      },
    ],
    ...rest,
  }
}
