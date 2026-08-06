import { afterEach, describe, expect, it } from 'vitest'
import { render, screen, act } from '@testing-library/react'
import { CartProvider } from './CartProvider'
import { useCart } from './cartContext'
import { readCart } from './storage'

/**
 * The cart across a reload.
 *
 * A mounted `CartProvider` is what a page load looks like, so a test that mounts
 * one against a populated `sessionStorage` is testing the thing that actually
 * matters: an F5 with a basket on the screen. It costs a re-scan today; after a
 * completed sale is submitted it costs the key that keeps the retry safe.
 */
describe('CartProvider', () => {
  const water = {
    productId: 'p-water',
    name: 'Still Water 500ml',
    sku: 'SKU-1001',
    unit: 'Each' as const,
    unitPrice: 1.2,
  }

  afterEach(() => {
    sessionStorage.clear()
  })

  /** Reads the cart out, and can add to it — a page, in miniature. */
  function Probe() {
    const { cart, dispatch } = useCart()

    return (
      <div>
        <p data-testid="lines">{cart.lines.length}</p>
        <p data-testid="sale-key">{cart.saleKey ?? 'none'}</p>
        <p data-testid="flashed">{cart.flashedKey ?? 'none'}</p>
        <button
          type="button"
          onClick={() => {
            dispatch({ type: 'add', product: water })
          }}
        >
          Scan
        </button>
        <button
          type="button"
          onClick={() => {
            dispatch({ type: 'beginSale' })
          }}
        >
          Tender
        </button>
      </div>
    )
  }

  function mount() {
    return render(
      <CartProvider>
        <Probe />
      </CartProvider>,
    )
  }

  it('brings the basket and the sale key back after a reload', () => {
    const first = mount()

    act(() => {
      screen.getByRole('button', { name: 'Scan' }).click()
      screen.getByRole('button', { name: 'Tender' }).click()
    })

    const key = screen.getByTestId('sale-key').textContent

    expect(key).not.toBe('none')

    // Unmount and mount again: a reload, as far as this tree can tell.
    first.unmount()
    mount()

    expect(screen.getByTestId('lines')).toHaveTextContent('1')

    // The part that costs money. A fresh GUID here would make the retry after a
    // dropped response new work, and the customer would pay twice.
    expect(screen.getByTestId('sale-key')).toHaveTextContent(key ?? '')
  })

  it('does not bring a flash back with it', () => {
    const first = mount()

    act(() => {
      screen.getByRole('button', { name: 'Scan' }).click()
    })

    expect(screen.getByTestId('flashed')).not.toHaveTextContent('none')

    first.unmount()
    mount()

    // The scan that caused it happened before the page went away.
    expect(screen.getByTestId('flashed')).toHaveTextContent('none')
  })

  it('starts empty when storage holds nothing, rather than failing to render', () => {
    mount()

    expect(screen.getByTestId('lines')).toHaveTextContent('0')
    expect(screen.getByTestId('sale-key')).toHaveTextContent('none')
  })

  it('writes on every change, not only when a sale is submitted', () => {
    // The difference between "a reload during payment is safe" and "a reload is
    // safe". Twenty scanned items is a minute of a queue's time.
    mount()

    expect(readCart()?.lines ?? []).toHaveLength(0)

    act(() => {
      screen.getByRole('button', { name: 'Scan' }).click()
    })

    expect(readCart()?.lines).toHaveLength(1)
  })
})
