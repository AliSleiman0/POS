// First, before anything that imports the generated client — see fetchMock.ts.
import { installFetchHandler, requestUrl, resetFetchHandler } from '@/test/fetchMock'

import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { ToastProvider } from '@/components/toast'
import { RefundDialog } from './RefundDialog'
import type { Sale } from './queries'

/**
 * Refunding, and the one rule that costs a customer money if it is wrong.
 *
 * **The idempotency key is minted when the dialog opens and reused on every
 * attempt** (CLAUDE.md invariant 6). A key generated per press makes the header
 * decorative: the first attempt times out, the cashier presses again, and the
 * shop pays out twice. The server cannot tell those apart — the whole point of
 * the key is that it can.
 */
describe('RefundDialog', () => {
  let queryClient: QueryClient
  let keys: string[]
  let attempts: number

  beforeEach(() => {
    queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
    })

    keys = []
    attempts = 0

    localStorage.setItem('pos.registerId', REGISTER_ID)

    installFetchHandler((input, init) => {
      const url = requestUrl(input)

      if (url.includes('/shifts/current')) {
        return Promise.resolve(
          Response.json({ id: SHIFT_ID, registerId: REGISTER_ID, status: 'Open', openingFloat: 100 }),
        )
      }

      if (url.includes('/refund')) {
        // From whichever of the two the client used: `openapi-fetch` may hand
        // the headers over on the init or already folded into a `Request`.
        const headers =
          input instanceof Request ? input.headers : new Headers(init?.headers)

        keys.push(headers.get('Idempotency-Key') ?? '')
        attempts++

        // The first attempt fails the way a dropped connection does. The second
        // succeeds — which is exactly the sequence the key exists to make safe.
        return attempts === 1
          ? Promise.resolve(new Response('', { status: 503 }))
          : Promise.resolve(Response.json({ id: 'refund-id', saleNumber: 44 }, { status: 201 }))
      }

      throw new Error(`Unexpected request: ${url}`)
    })
  })

  afterEach(() => {
    resetFetchHandler()
    localStorage.clear()
  })

  it('reuses one idempotency key across every attempt', async () => {
    const user = userEvent.setup()

    renderDialog(queryClient)

    await user.type(screen.getByLabelText(/Reason/), 'Faulty')

    const refund = screen.getByRole('button', { name: 'Refund' })

    await user.click(refund)
    await waitFor(() => {
      expect(attempts).toBe(1)
    })

    // Still open, with the reason as it was — so pressing again is a retry
    // rather than a rebuild.
    await user.click(screen.getByRole('button', { name: 'Refund' }))
    await waitFor(() => {
      expect(attempts).toBe(2)
    })

    expect(keys).toHaveLength(2)
    expect(keys[0]).toBe(keys[1])
    expect(keys[0]).not.toBe('')
  })

  it('will not submit without a reason', async () => {
    // Required in the UI as well as at the API: in six months this is the only
    // explanation of why the money went back.
    renderDialog(queryClient)

    expect(screen.getByRole('button', { name: 'Refund' })).toBeDisabled()
  })

  it('says so when no drawer is open rather than failing at the server', async () => {
    resetFetchHandler()
    installFetchHandler((input) => {
      const url = requestUrl(input)

      if (url.includes('/shifts/current')) {
        // A 404 is how the API says "no drawer is open".
        return Promise.resolve(new Response('', { status: 404 }))
      }

      throw new Error(`Unexpected request: ${url}`)
    })

    const user = userEvent.setup()

    renderDialog(queryClient)

    await screen.findByText(/No drawer is open/)

    await user.type(screen.getByLabelText(/Reason/), 'Faulty')

    // The cash comes out of the drawer that is open now. Without one there is
    // nowhere for it to come from, so the button stays disabled.
    expect(screen.getByRole('button', { name: 'Refund' })).toBeDisabled()
  })

  it('opens no blocking browser dialog', () => {
    // Invariant 10, at the screen a customer is standing in front of.
    renderDialog(queryClient)

    expect(screen.getByRole('dialog')).toBeInTheDocument()
  })
})

const REGISTER_ID = '00000000-0000-0000-0000-0000000000aa'
const SHIFT_ID = '00000000-0000-0000-0000-0000000000bb'

function renderDialog(queryClient: QueryClient) {
  return render(
    <QueryClientProvider client={queryClient}>
      <ToastProvider>
        <RefundDialog
          sale={sale()}
          currency="EUR"
          onClose={() => undefined}
          onRefunded={() => undefined}
        />
      </ToastProvider>
    </QueryClientProvider>,
  )
}

function sale(): Sale {
  return {
    id: '00000000-0000-0000-0000-0000000000cc',
    saleNumber: 42,
    type: 'Sale',
    status: 'Completed',
    subtotal: 10,
    discountTotal: 0,
    taxTotal: 2.3,
    roundingAdjustment: 0,
    total: 12.3,
    changeGiven: 0,
    completedAt: '2026-08-07T12:00:00+00:00',
    lines: [
      {
        id: '00000000-0000-0000-0000-0000000000dd',
        productId: '00000000-0000-0000-0000-0000000000ee',
        lineNumber: 1,
        description: 'Coffee 250g',
        quantity: 2,
        unitPrice: 5,
        taxRate: 0.23,
        discountAmount: 0,
        lineSubtotal: 10,
        lineTax: 2.3,
        lineTotal: 12.3,
        isPriceOverridden: false,
      },
    ],
    tenders: [{ method: 'Cash', amount: 12.3, changeGiven: null }],
    registerId: REGISTER_ID,
    shiftId: SHIFT_ID,
    cashierId: '00000000-0000-0000-0000-0000000000ff',
    cashierName: 'Ada Byrne',
    registerName: 'Front Counter',
    taxMode: 'Exclusive',
    originalSaleId: null,
    originalSaleNumber: null,
    voidedAt: null,
    voidedByName: null,
    voidReason: null,
    refundReason: null,
    refunds: [],
  }
}
