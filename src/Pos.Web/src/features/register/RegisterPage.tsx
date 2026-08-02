import { useCallback, useRef, useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { api, unwrap } from '@/api/client'
import { isProblemError } from '@/api/problem'
import { CATALOG_STALE_MS } from '@/app/queryClient'
import { useAuth } from '@/auth/authContext'
import { Button } from '@/components/ui/button'
import { ErrorState } from '@/components/states'
import { useToast } from '@/components/toastContext'
import { isStorableAmount } from '@/features/catalog/validation'
import { beep, isScanSoundMuted, setScanSoundMuted } from '@/lib/beep'
import { formatMoney, parseServerDecimal, type ServerDecimal } from '@/lib/money'
import { MAX_LINE_QUANTITY, provisionalLineMinor, type CartProduct } from './cart'
import { useCart } from './cartContext'
import { CartPane } from './CartPane'
import { OpenShiftPanel } from './OpenShiftPanel'
import { ProductGrid } from './ProductGrid'
import { registerKeys, useCurrentShift, useQuote } from './queries'
import { TotalPanel } from './TotalPanel'
import { useScanner } from './useScanner'

/**
 * The register.
 *
 * Three regions: the cart (the focus), the total and keypad, and a searchable
 * grid for goods that never had a barcode. Everything is reachable from the
 * keyboard, because scanners *are* keyboards and experienced staff do not touch
 * the screen for common actions.
 *
 * **Keyboard map.** Printable characters always belong to the scan/keypad
 * buffer — that is what lets one handler serve a wedge scanner and a person
 * typing a quantity — so the shortcuts are keys a barcode cannot contain:
 *
 * | Key | Action |
 * |---|---|
 * | ↑ / ↓ | move the line selection |
 * | `+` / `−` | quantity ±1 on the selected line |
 * | digits, `.` | quantity for the selected line; `Enter` commits |
 * | `Backspace` | edit the quantity being typed |
 * | `Delete` | void the selected line |
 * | `Escape` | clear the entry and dismiss the scan banner |
 * | `F2` | focus the product search |
 *
 * A fast burst of characters ending in Enter is a scan and goes to the cart
 * instead; see `lib/scanner.ts` for how the two are told apart.
 */
export function RegisterPage() {
  const { status, tenant } = useAuth()
  const currency = tenant?.currencyCode ?? 'GBP'
  const queryClient = useQueryClient()
  const toast = useToast()

  const { cart, dispatch } = useCart()
  const shift = useCurrentShift()
  const quote = useQuote(cart)

  const [pending, setPending] = useState('')
  const [unknownCode, setUnknownCode] = useState<string | null>(null)
  const [search, setSearch] = useState('')
  const [muted, setMuted] = useState(isScanSoundMuted)
  const searchRef = useRef<HTMLInputElement>(null)

  /** Line totals from the quote, by product, for the cart rows. */
  const pricedLines = new Map<string, ServerDecimal>(
    (quote.data?.lines ?? []).map((line) => [line.productId, line.lineTotal]),
  )

  const provisionalMinor = cart.lines.reduce((sum, line) => sum + provisionalLineMinor(line), 0)

  const addProduct = useCallback(
    (product: CartProduct) => {
      dispatch({ type: 'add', product })
      beep('ok')
      // The flash is a one-shot: cleared straight after so the next scan of the
      // same line animates again rather than sitting permanently highlighted.
      setTimeout(() => {
        dispatch({ type: 'clearFlash' })
      }, 400)
    },
    [dispatch],
  )

  /**
   * A scanned code becomes a cart line.
   *
   * `GET /products/by-barcode/{code}` answers with the product, its price and
   * its tax rate in one statement. `fetchQuery` rather than a hook because a
   * scan is an event, not a render: there is no code to subscribe to until a
   * keystroke arrives.
   */
  const onScan = useCallback(
    async (code: string) => {
      try {
        const product = await queryClient.fetchQuery({
          queryKey: registerKeys.barcode(code),
          queryFn: () =>
            unwrap(api.GET('/api/v1/products/by-barcode/{code}', { params: { path: { code } } })),
          staleTime: CATALOG_STALE_MS,
        })

        // A deactivated product still scans, by design, so the till can say
        // this rather than "unknown code". Selling one is refused server-side.
        if (!product.isActive) {
          beep('miss')
          toast.show(`${product.name} is not for sale.`, {
            tone: 'error',
            detail: 'It has been deactivated in the catalog.',
          })
          return
        }

        addProduct({
          productId: product.productId,
          name: product.name,
          sku: product.sku,
          unit: product.unit,
          unitPrice: product.unitPrice,
        })
      } catch (caught) {
        beep('miss')

        // Unknown code: a banner, never a modal. A dialog here blocks the queue
        // and the next scan lands in whatever has focus afterwards.
        if (isProblemError(caught) && caught.status === 404) {
          setUnknownCode(code)
          return
        }

        toast.showError(caught, 'Could not look that code up.')
      }
    },
    [addProduct, queryClient, toast],
  )

  /** Enter on something a person typed: a quantity for the selected line. */
  const onManual = useCallback(
    (text: string) => {
      setPending('')

      if (!isStorableAmount(text)) {
        beep('miss')
        toast.show(`"${text}" is not a quantity.`, { tone: 'error' })
        return
      }

      /*
       * A barcode that arrived too slowly to be classified as one.
       *
       * Clamping this to the maximum quantity would sell 9,999 bottles of water
       * because a scan was delivered while the page was busy — so it is refused
       * outright, and the message says what to do about it. The number is well
       * past any real quantity and well below any barcode.
       */
      if (Number(text) > MAX_LINE_QUANTITY) {
        beep('miss')
        toast.show('That looked like a barcode, not a quantity.', {
          tone: 'error',
          detail: 'Nothing was changed. Scan it again.',
        })
        return
      }

      if (cart.selectedKey === null) {
        beep('miss')
        toast.show('Select a line first, then type its quantity.', { tone: 'info' })
        return
      }

      dispatch({ type: 'setQuantity', key: cart.selectedKey, quantity: Number(text) })
    },
    [cart.selectedKey, dispatch, toast],
  )

  const onKey = useCallback(
    (event: KeyboardEvent) => {
      switch (event.key) {
        case 'ArrowDown':
          event.preventDefault()
          dispatch({ type: 'move', delta: 1 })
          break
        case 'ArrowUp':
          event.preventDefault()
          dispatch({ type: 'move', delta: -1 })
          break
        case 'Delete':
          if (cart.selectedKey !== null) {
            event.preventDefault()
            dispatch({ type: 'remove', key: cart.selectedKey })
          }
          break
        case 'Escape':
          setPending('')
          setUnknownCode(null)
          break
        case 'F2':
          event.preventDefault()
          searchRef.current?.focus()
          break
        default:
          break
      }
    },
    [cart.selectedKey, dispatch],
  )

  useScanner(status === 'authenticated', {
    onScan: (code) => {
      void onScan(code)
    },
    onDuplicate: () => {
      // The scanner fired twice for one item. Saying nothing would look like a
      // missed scan and invite a third; this says "heard you, once".
      toast.show('Same code again — counted once.', { tone: 'info' })
    },
    onManual,
    onKey,
    onPendingChange: setPending,
    // `+` and `-` step the selected line: a barcode cannot contain either, so
    // they are safe to reserve.
    reservedKeys: {
      '+': () => {
        if (cart.selectedKey !== null) {
          dispatch({ type: 'adjustQuantity', key: cart.selectedKey, delta: 1 })
        }
      },
      '-': () => {
        if (cart.selectedKey !== null) {
          dispatch({ type: 'adjustQuantity', key: cart.selectedKey, delta: -1 })
        }
      },
    },
  })

  return (
    <div className="flex h-full min-h-0 flex-col gap-3 p-3">
      <header className="flex flex-wrap items-center justify-between gap-3">
        <div className="flex items-baseline gap-3">
          <h1 className="text-lg font-semibold text-foreground">Register</h1>
          <ShiftLine shift={shift} currency={currency} />
        </div>

        <Button
          variant="outline"
          size="sm"
          aria-pressed={muted}
          onClick={() => {
            const next = !muted
            setScanSoundMuted(next)
            setMuted(next)
            if (!next) {
              beep('ok')
            }
          }}
        >
          {muted ? 'Scan sound off' : 'Scan sound on'}
        </Button>
      </header>

      {unknownCode !== null ? (
        <UnknownCodeBanner
          code={unknownCode}
          onSearch={() => {
            setSearch(unknownCode)
            setUnknownCode(null)
            searchRef.current?.focus()
          }}
          onDismiss={() => {
            setUnknownCode(null)
          }}
        />
      ) : null}

      <div className="grid min-h-0 flex-1 grid-cols-1 gap-3 lg:grid-cols-[minmax(0,1.5fr)_minmax(340px,1fr)]">
        <CartPane
          currency={currency}
          pricedLines={pricedLines}
          isQuoting={quote.isFetching && !quote.isPending}
        />

        <div className="flex min-h-0 flex-col gap-3">
          {shift.isPending ? null : shift.isSuccess ? (
            <TotalPanel
              cart={cart}
              quote={quote.data}
              currency={currency}
              provisionalMinor={provisionalMinor}
              isQuoting={quote.isFetching}
              quoteFailed={quote.isError}
              pending={pending}
            />
          ) : (
            // A 404 from `/shifts/current` is the answer "no drawer is open".
            <OpenShiftPanel registerId={shift.registerId} currency={currency} />
          )}

          {quote.isError ? (
            <ErrorState
              error={quote.error}
              title="The server could not price this cart."
              onRetry={() => {
                void quote.refetch()
              }}
            />
          ) : null}

          <ProductGrid
            search={search}
            onSearchChange={setSearch}
            searchRef={searchRef}
            currency={currency}
            onPick={addProduct}
          />
        </div>
      </div>
    </div>
  )
}

/** The drawer, in a line: the thing anyone standing at a till needs to know. */
function ShiftLine({
  shift,
  currency,
}: {
  shift: ReturnType<typeof useCurrentShift>
  currency: string
}) {
  if (shift.registerId === null) {
    return <span className="text-sm text-muted-foreground">This browser is not a till</span>
  }

  if (shift.isPending) {
    return <span className="text-sm text-muted-foreground">Checking the drawer…</span>
  }

  if (shift.isError) {
    return <span className="text-sm font-medium text-destructive">Drawer closed</span>
  }

  return (
    <span className="text-sm text-muted-foreground">
      Drawer open · float {formatMoney(shift.data.openingFloat, currency)}
      {parseServerDecimal(shift.data.openingFloat) === 0 ? ' (empty)' : ''}
    </span>
  )
}

/**
 * A code the catalog does not know.
 *
 * In the page, dismissible, and it does not take focus — a modal here stalls the
 * queue, and `alert()` is forbidden outright (CLAUDE.md invariant 10): it blocks
 * the event loop, so a scanner firing behind it queues its keystrokes and
 * replays them into whatever has focus once it closes.
 */
function UnknownCodeBanner({
  code,
  onSearch,
  onDismiss,
}: {
  code: string
  onSearch: () => void
  onDismiss: () => void
}) {
  return (
    <div
      role="status"
      data-testid="unknown-code"
      className="flex flex-wrap items-center gap-3 rounded-lg border border-destructive/30 bg-destructive/5 px-4 py-2.5"
    >
      <p className="flex-1 text-sm text-foreground">
        <span className="font-medium">Unknown item.</span> Nothing in the catalog has the code{' '}
        <span className="font-mono">{code}</span>.
      </p>
      <Button variant="outline" size="sm" onClick={onSearch}>
        Search for it
      </Button>
      <Button variant="ghost" size="sm" onClick={onDismiss}>
        Dismiss
      </Button>
    </div>
  )
}
