// First, before anything that imports the generated client — see fetchMock.ts.
import { installFetchHandler, requestUrl, resetFetchHandler } from '@/test/fetchMock'

import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { CartProvider } from './CartProvider'
import { useCart } from './cartContext'
import { OverrideProvider } from './OverrideProvider'
import { useOverride } from './overrideContext'

const DEVICE_TOKEN_KEY = 'pos.deviceToken'

/*
 * Stand-ins, deliberately not shaped like the real thing.
 *
 * A device token and a grant are both `base64url(tenantId).base64url(32 bytes)`,
 * and a literal of that shape in a committed file is what a secret scanner is
 * for — it cannot tell a fixture from a leak, and it is right not to try. These
 * are never parsed: `fetchMock` returns canned responses, and the assertion
 * below only needs a string distinctive enough to search storage for.
 */
const FAKE_DEVICE_TOKEN = 'test-device-token-not-a-credential'
const FAKE_GRANT = 'test-grant-not-a-credential'

/**
 * Holding a manager's authorisation.
 *
 * Two things are being pinned. The first is that the grant **never reaches
 * storage**: it is a live credential, and 5.5 is about to add `sessionStorage`
 * persistence to the cart it sits beside. The second is that a refusal changes
 * nothing — the caller awaits a promise, and "no" has to be a value it can act
 * on rather than an exception somewhere else.
 */
describe('OverrideProvider', () => {
  let queryClient: QueryClient

  beforeEach(() => {
    queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
    })

    localStorage.clear()
    sessionStorage.clear()

    // The device token is the second factor. Without it the dialog says so
    // instead of offering a PIN pad.
    localStorage.setItem(DEVICE_TOKEN_KEY, FAKE_DEVICE_TOKEN)
  })

  afterEach(() => {
    resetFetchHandler()
    localStorage.clear()
    sessionStorage.clear()
  })

  /** A cashier who presses Discount, and whatever came back from doing so. */
  function Harness({ onResult }: { onResult: (value: string | null) => void }) {
    const { authorization, authorize } = useOverride()
    const { cart, dispatch } = useCart()

    return (
      <div>
        <button
          type="button"
          onClick={() => {
            // Something in the cart, so the provider does not clear the grant
            // as "the next customer".
            dispatch({
              type: 'add',
              product: {
                productId: 'p-water',
                name: 'Still Water 500ml',
                sku: 'SKU-1001',
                unit: 'Each',
                unitPrice: 1.2,
              },
            })

            void authorize(['CanApplyDiscount']).then((result) => {
              onResult(result?.grant ?? null)
            })
          }}
        >
          Discount
        </button>
        <p data-testid="held">{authorization?.authorizedByName ?? 'nobody'}</p>
        <p data-testid="lines">{cart.lines.length}</p>
      </div>
    )
  }

  function renderHarness(onResult: (value: string | null) => void = () => undefined) {
    return render(
      <QueryClientProvider client={queryClient}>
        <CartProvider>
          <OverrideProvider>
            <Harness onResult={onResult} />
          </OverrideProvider>
        </CartProvider>
      </QueryClientProvider>,
    )
  }

  const STAFF = [
    { id: 'u-manager', displayName: 'Sam Cole' },
    { id: 'u-cashier', displayName: 'Robin Vale' },
  ]

  function respond(overrideResponse: () => Response) {
    installFetchHandler((input) => {
      const url = requestUrl(input)

      if (url.includes('/employees/pin-eligible')) {
        return Promise.resolve(Response.json(STAFF))
      }

      if (url.includes('/auth/override')) {
        return Promise.resolve(overrideResponse())
      }

      return Promise.reject(new Error(`Unexpected request: ${url}`))
    })
  }

  it('holds a grant a manager gave, and never writes it to storage', async () => {
    respond(() =>
      Response.json({
        grant: FAKE_GRANT,
        expiresIn: 300,
        authorizedById: 'u-manager',
        authorizedByName: 'Sam Cole',
        policies: ['CanApplyDiscount'],
      }),
    )

    let granted: string | null | undefined
    const user = userEvent.setup()

    renderHarness((value) => {
      granted = value
    })

    await user.click(screen.getByRole('button', { name: 'Discount' }))

    // A cashier does not reach an amount field on their own authority: the PIN
    // step is what a press of Discount gets them.
    expect(await screen.findByTestId('manager-override')).toBeInTheDocument()

    await user.click(await screen.findByRole('button', { name: 'Sam Cole' }))
    await user.type(screen.getByLabelText('Manager PIN'), '7391')
    await user.click(screen.getByRole('button', { name: 'Approve' }))

    await waitFor(() => {
      expect(screen.getByTestId('held')).toHaveTextContent('Sam Cole')
    })

    expect(granted).toBe(FAKE_GRANT)

    /*
     * The assertion this file exists for.
     *
     * The grant sits next to a cart that 5.5 will persist to sessionStorage. If
     * it ever moves into the cart reducer, or somebody adds a convenience
     * "remember the authorisation" line, this goes red — and the failure names
     * the real problem, which is a live credential written to a shared tablet's
     * disk.
     */
    const stored = [...Object.values(sessionStorage), ...Object.values(localStorage)].join(' ')

    expect(stored).not.toContain(FAKE_GRANT)
  })

  it('changes nothing when the manager declines', async () => {
    respond(() => Response.json({}))

    let granted: string | null | undefined = undefined
    const user = userEvent.setup()

    renderHarness((value) => {
      granted = value
    })

    await user.click(screen.getByRole('button', { name: 'Discount' }))
    await user.click(await screen.findByRole('button', { name: 'Cancel' }))

    await waitFor(() => {
      expect(granted).toBeNull()
    })

    // Resolved rather than left hanging, and nothing was held: a caller that
    // awaited this can return without touching the cart.
    expect(screen.getByTestId('held')).toHaveTextContent('nobody')
    expect(screen.queryByTestId('manager-override')).not.toBeInTheDocument()
  })

  it('says who cannot authorise it, rather than claiming the PIN was wrong', async () => {
    // A manager told "that PIN was not recognised" retries their own correct PIN
    // until they are locked out. The server distinguishes the two cases and so
    // does this.
    respond(
      () =>
        new Response(
          JSON.stringify({
            type: 'https://pos.example/errors/override-not-permitted',
            title: 'Not permitted to authorise',
          }),
          { status: 403, headers: { 'Content-Type': 'application/problem+json' } },
        ),
    )

    const user = userEvent.setup()

    renderHarness()

    await user.click(screen.getByRole('button', { name: 'Discount' }))
    await user.click(await screen.findByRole('button', { name: 'Robin Vale' }))
    await user.type(screen.getByLabelText('Manager PIN'), '4821')
    await user.click(screen.getByRole('button', { name: 'Approve' }))

    expect(await screen.findByText('Robin Vale cannot authorise this.')).toBeInTheDocument()
    expect(screen.getByTestId('held')).toHaveTextContent('nobody')
  })

  it('forgets the authorisation when the cart empties, so it cannot reach the next customer', async () => {
    respond(() =>
      Response.json({
        grant: FAKE_GRANT,
        expiresIn: 300,
        authorizedById: 'u-manager',
        authorizedByName: 'Sam Cole',
        policies: ['CanApplyDiscount'],
      }),
    )

    const user = userEvent.setup()

    function Voider() {
      const { dispatch } = useCart()

      return (
        <button
          type="button"
          onClick={() => {
            dispatch({ type: 'clear' })
          }}
        >
          Void cart
        </button>
      )
    }

    render(
      <QueryClientProvider client={queryClient}>
        <CartProvider>
          <OverrideProvider>
            <Harness onResult={() => undefined} />
            <Voider />
          </OverrideProvider>
        </CartProvider>
      </QueryClientProvider>,
    )

    await user.click(screen.getByRole('button', { name: 'Discount' }))
    await user.click(await screen.findByRole('button', { name: 'Sam Cole' }))
    await user.type(screen.getByLabelText('Manager PIN'), '7391')
    await user.click(screen.getByRole('button', { name: 'Approve' }))

    await waitFor(() => {
      expect(screen.getByTestId('held')).toHaveTextContent('Sam Cole')
    })

    await user.click(screen.getByRole('button', { name: 'Void cart' }))

    await waitFor(() => {
      expect(screen.getByTestId('held')).toHaveTextContent('nobody')
    })
  })
})
