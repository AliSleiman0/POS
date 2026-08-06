import { useCallback, useRef, useState } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { api, unwrap } from '@/api/client'
import { ErrorType, isProblemError } from '@/api/problem'
import { CATALOG_STALE_MS } from '@/app/queryClient'
import { useAuth } from '@/auth/authContext'
import { Button } from '@/components/ui/button'
import { ErrorState } from '@/components/states'
import { useToast } from '@/components/toastContext'
import { isStorableAmount } from '@/features/catalog/validation'
import { beep, isScanSoundMuted, setScanSoundMuted } from '@/lib/beep'
import { formatMoney, parseServerDecimal, type ServerDecimal } from '@/lib/money'
import {
  isEmpty,
  MAX_LINE_QUANTITY,
  provisionalLineMinor,
  type CartLine,
  type CartProduct,
} from './cart'
import { useCart } from './cartContext'
import { CartPane } from './CartPane'
import { LineAdjustDialog, type Adjustment } from './LineAdjustDialog'
import { OpenShiftPanel } from './OpenShiftPanel'
import { useOverride } from './overrideContext'
import { ProductGrid } from './ProductGrid'
import {
  fetchSaleByKey,
  registerKeys,
  useCompleteSale,
  useCurrentShift,
  useQuote,
  type CompletedSale,
} from './queries'
import { SaleCompletePanel } from './SaleCompletePanel'
import { clearSaleInFlight, writeSaleInFlight } from './storage'
import type { Tender } from './tender'
import { TenderPanel } from './TenderPanel'
import { TotalPanel } from './TotalPanel'
import { useSaleRecovery } from './useSaleRecovery'
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
 * | `Escape` | clear the entry, dismiss the scan banner, leave the tender step |
 * | `F2` | focus the product search |
 * | `F3` | discount the selected line |
 * | `F4` | change the selected line's price |
 * | `F7` | take cash |
 *
 * A fast burst of characters ending in Enter is a scan and goes to the cart
 * instead; see `lib/scanner.ts` for how the two are told apart.
 *
 * **While tendering the same keys mean money.** Digits then `Enter` add a cash
 * amount rather than a quantity, and the keys that edit the basket — ↑/↓,
 * `Delete`, `+`/`−` — stand down, because a line voided by a stray keystroke
 * would change a total that has already been read out to the customer.
 */
export function RegisterPage() {
  const { status, tenant, can } = useAuth()
  const currency = tenant?.currencyCode ?? 'GBP'
  const queryClient = useQueryClient()
  const toast = useToast()

  const { cart, dispatch } = useCart()
  const override = useOverride()
  const shift = useCurrentShift()
  const quote = useQuote(cart, override.authorization?.grant ?? null)

  const [pending, setPending] = useState('')
  const [unknownCode, setUnknownCode] = useState<string | null>(null)
  const [search, setSearch] = useState('')
  const [muted, setMuted] = useState(isScanSoundMuted)
  const [adjusting, setAdjusting] = useState<{
    adjustment: Adjustment
    line: CartLine | null
  } | null>(null)
  const searchRef = useRef<HTMLInputElement>(null)

  /**
   * Which of the three things the right-hand column is doing.
   *
   * `building` → the total and keypad; `tendering` → the tender pad;
   * `complete` → the change due. The cart pane is unaffected by all three, which
   * is the point of putting them here rather than over the screen.
   */
  const [tendering, setTendering] = useState(false)
  const [tenders, setTenders] = useState<Tender[]>([])
  const [completed, setCompleted] = useState<CompletedSale | null>(null)

  const completeSale = useCompleteSale()

  /**
   * A payment that was in progress when this page went away.
   *
   * Resolved by asking the server what the key bought, not by submitting it
   * again — see `useSaleRecovery`. Both outcomes land in the states above: a
   * sale that was taken becomes `completed`, one that was not restores the
   * tender pad with its amounts so a single press finishes it.
   */
  const { recovery, retry: retryRecovery } = useSaleRecovery({
    enabled: status === 'authenticated',
    registerId: shift.registerId,
    onTaken: (sale) => {
      setCompleted({ sale, provenance: 'recovered' })
      setTendering(false)
      setTenders([])
      dispatch({ type: 'clear' })
    },
    onNotTaken: (record) => {
      setTenders([...record.tenders])
      setTendering(true)
      toast.show('That payment was not taken.', {
        tone: 'info',
        detail: 'Nothing was charged. The amounts are as they were — complete it or start again.',
      })
    },
  })

  /**
   * Enters the tender step.
   *
   * `beginSale` is what mints the sale's identity, and it happens **here** —
   * when the sale begins — rather than when Complete is pressed. That is
   * CLAUDE.md invariant 6 in one line: every attempt from this moment carries
   * the same key, so a retry after a dropped connection returns the original
   * sale instead of taking the money again. It is idempotent, so backing out and
   * coming in again is still one sale.
   */
  const beginTender = useCallback(() => {
    dispatch({ type: 'beginSale' })
    setTenders([])
    setPending('')
    setTendering(true)
  }, [dispatch])

  const cancelTender = useCallback(() => {
    // The tenders go; the sale key stays. Nothing was taken, but this is still
    // the same sale being tendered — a new key here would defeat the mechanism
    // for anyone who backs out to remove a line and comes straight back.
    setTenders([])
    setPending('')
    setTendering(false)
  }, [])

  const addTender = useCallback((amountMinor: number) => {
    setTenders((current) => [...current, { key: crypto.randomUUID(), amountMinor }])
    setPending('')
  }, [])

  /** Line totals from the quote, by product, for the cart rows. */
  const pricedLines = new Map<string, ServerDecimal>(
    (quote.data?.lines ?? []).map((line) => [line.productId, line.lineTotal]),
  )

  const provisionalMinor = cart.lines.reduce((sum, line) => sum + provisionalLineMinor(line), 0)

  const addProduct = useCallback(
    (product: CartProduct) => {
      // The next customer. Their first item is what clears the last one's change
      // off the screen — no dismiss button, because a queue does not wait for
      // one.
      setCompleted(null)
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

  /** Enter on something a person typed: a quantity, or a cash amount. */
  const onManual = useCallback(
    (text: string) => {
      setPending('')

      // While tendering, the same keystream means money instead of quantity.
      // One buffer rather than two competing handlers, for the same reason the
      // scanner and the keypad share one — see `lib/scanner.ts`.
      if (tendering) {
        if (!isStorableAmount(text)) {
          beep('miss')
          toast.show(`"${text}" is not an amount.`, { tone: 'error' })
          return
        }

        addTender(Math.round(Number(text) * 100))
        return
      }

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
    [addTender, cart.selectedKey, dispatch, tendering, toast],
  )

  /**
   * Opens a discount or price-override entry, asking a manager first if the
   * cashier cannot approve it themselves.
   *
   * **A Cashier is not shown a control that does nothing, and not shown one that
   * discounts on their own authority either.** Pressing Discount without
   * `CanApplyDiscount` goes to the manager's PIN first, every time, and if the
   * manager declines nothing is changed — the amount field is never reached. The
   * server refuses the same cart regardless (`override-required`), so this is
   * the courteous path to the same answer rather than the enforcement.
   */
  const beginAdjustment = useCallback(
    async (adjustment: Adjustment, line: CartLine | null) => {
      const policy = adjustment === 'priceOverride' ? 'CanOverridePrice' : 'CanApplyDiscount'

      if (!can(policy) && (await override.authorize([policy])) === null) {
        return
      }

      setAdjusting({ adjustment, line })
    },
    [can, override],
  )

  const applyAdjustment = useCallback(
    (amount: number | null) => {
      if (adjusting === null) {
        return
      }

      const { adjustment, line } = adjusting

      setAdjusting(null)

      if (adjustment === 'cartDiscount') {
        dispatch({ type: 'setCartDiscount', amount })
        return
      }

      if (line === null) {
        return
      }

      dispatch(
        adjustment === 'lineDiscount'
          ? { type: 'setLineDiscount', key: line.key, amount }
          : { type: 'setPriceOverride', key: line.key, unitPrice: amount },
      )
    },
    [adjusting, dispatch],
  )

  /** The selected line, re-read from the cart so it is never a stale copy. */
  const selectedLine = cart.lines.find((line) => line.key === cart.selectedKey) ?? null

  /**
   * Finds out what a key that has already been spent actually bought.
   *
   * Reached only from `idempotency-key-reused`, which is the server saying "this
   * key wrote something, and it was not this basket". The sale it wrote is shown
   * — the cashier has to know a payment was taken before they take another — and
   * what is on the screen now gets an identity of its own so it can still be
   * sold.
   */
  const resolveSpentKey = useCallback(
    async (saleKey: string) => {
      // Whatever comes back, this basket is not that sale, and pressing Complete
      // again must not repeat the same refusal.
      dispatch({ type: 'restartSale' })

      try {
        const sale = await fetchSaleByKey(saleKey)

        if (sale === null) {
          // The key bought nothing under /sales — it was spent on some other
          // idempotent route. Nothing to show, but the cart is usable again.
          toast.show('That sale had already been submitted.', {
            tone: 'error',
            detail: 'Nothing on this screen was charged. Take the payment again.',
          })
          return
        }

        setCompleted({ sale, provenance: 'recovered' })
        setTendering(false)
        setTenders([])

        toast.show(`Sale #${String(sale.saleNumber ?? '—')} was already paid for.`, {
          tone: 'error',
          detail: 'The basket has changed since. Check that sale before charging again.',
        })
      } catch (caught) {
        toast.showError(caught, 'That sale was already submitted, and it cannot be looked up.')
      }
    },
    [dispatch, toast],
  )

  /**
   * Sends the sale, and says what happened when it does not go.
   *
   * Every branch below leaves the cart and the tenders **intact**. A failed
   * submit that cleared the screen would make the cashier rebuild the basket
   * with a customer waiting, and — worse — would lose the key that makes the
   * retry safe.
   */
  const onCompleteSale = useCallback(() => {
    const saleKey = cart.saleKey

    if (shift.data === undefined || shift.registerId === null || saleKey === null) {
      return
    }

    /*
     * Written before the request, and that ordering is the feature.
     *
     * If this page dies between here and the response — a reload, a crashed
     * tab, a tablet that slept — the record is what lets the till come back and
     * ask what the key bought instead of guessing. Written after the response
     * it would exist only in the cases that do not need it.
     */
    writeSaleInFlight({
      saleKey,
      tenders,
      registerId: shift.registerId,
      shiftId: shift.data.id,
      at: Date.now(),
    })

    completeSale.mutate(
      {
        cart,
        registerId: shift.registerId,
        shiftId: shift.data.id,
        tenders,
        grant: override.authorization?.grant ?? null,
      },
      {
        onSuccess: (result) => {
          clearSaleInFlight()

          setCompleted(result)
          setTendering(false)
          setTenders([])
          setPending('')

          // The sale is finished, so its identity and its authorisation are
          // both spent. `clear` drops the key; `OverrideProvider` drops the
          // grant when the cart empties.
          dispatch({ type: 'clear' })

          beep('ok')
        },

        onError: (caught) => {
          beep('miss')

          if (isProblemError(caught)) {
            /*
             * The key already bought something else.
             *
             * The server fingerprints the request body, so this answer means one
             * thing only: an earlier attempt with this key *landed*, and the
             * basket has changed since. That is exactly the case a cashier
             * reaches by retrying a submit whose response was lost and then
             * editing the cart — and until this branch existed it dead-ended on
             * "try again", which would have failed identically for ever.
             *
             * So: find out what it bought, show that, and give what is on the
             * screen now an identity of its own.
             */
            if (caught.is(ErrorType.idempotencyKeyReused)) {
              clearSaleInFlight()
              void resolveSpentKey(saleKey)
              return
            }

            /*
             * The manager's grant expired while the queue moved.
             *
             * It lives five minutes, and this is the first code path slow enough
             * to lose one — the cashier gets approval, scans more, counts notes,
             * and the authorisation dies between the quote and the sale. Said
             * plainly, with the way out, rather than as a bare "forbidden".
             */
            if (caught.is(ErrorType.overrideRequired)) {
              // Refused before anything was written, so there is nothing to
              // recover later and the record would only confuse the next load.
              clearSaleInFlight()
              override.clear()
              toast.show('That approval has expired.', {
                tone: 'error',
                detail: 'Nothing was charged. Ask a manager to approve it again.',
              })
              return
            }

            if (caught.is(ErrorType.shiftClosed)) {
              clearSaleInFlight()
              toast.show('The drawer was closed.', {
                tone: 'error',
                detail: 'Nothing was charged. Open a drawer and take the payment again.',
              })
              return
            }

            if (caught.is(ErrorType.underTender)) {
              // Should be unreachable behind the disabled button. Surfaced
              // rather than swallowed, because if it happens the button is lying.
              clearSaleInFlight()
              toast.show('That does not cover the total.', { tone: 'error' })
              return
            }
          }

          /*
           * Including the one this milestone is really about: a transport
           * failure. The cart, the tenders and the key are all still here, so
           * pressing Complete again is a retry rather than a second sale.
           *
           * **The in-flight record is deliberately kept.** This is the branch
           * where the till does not know whether the server wrote the sale, and
           * that record is the only thing that can answer it after a reload.
           */
          toast.showError(caught, 'Could not complete the sale. Try again.')
        },
      },
    )
  }, [
    cart,
    completeSale,
    dispatch,
    override,
    resolveSpentKey,
    shift.data,
    shift.registerId,
    tenders,
    toast,
  ])

  const onKey = useCallback(
    (event: KeyboardEvent) => {
      /*
       * While tendering, the keyboard is counting money, not editing the basket.
       *
       * ↑/↓ and Delete would otherwise still be moving the selection and voiding
       * lines under a cashier whose attention is on the notes in their hand —
       * and a line voided by a stray Delete at that moment changes the total
       * after it has been read out. The cart's own buttons stay live for a
       * deliberate change; only the invisible keystream is withdrawn.
       */
      if (
        tendering &&
        (event.key === 'ArrowDown' || event.key === 'ArrowUp' || event.key === 'Delete')
      ) {
        return
      }

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

          // Backing out of the tender step, which is what Escape means once the
          // cashier is in it. The cart and the sale's key both survive.
          if (tendering) {
            cancelTender()
          }
          break
        case 'F7':
          // A function key, like F2/F3/F4 — a barcode cannot contain one, so
          // reserving it cannot delete a character from the middle of a scan.
          if (!tendering && !isEmpty(cart) && quote.data !== undefined) {
            event.preventDefault()
            beginTender()
          }
          break
        case 'F2':
          event.preventDefault()
          searchRef.current?.focus()
          break
        case 'F3':
          // Function keys, like F2, because a barcode cannot contain one —
          // reserving a printable character would delete it from the middle of
          // a scanned code.
          if (selectedLine !== null) {
            event.preventDefault()
            void beginAdjustment('lineDiscount', selectedLine)
          }
          break
        case 'F4':
          if (selectedLine !== null) {
            event.preventDefault()
            void beginAdjustment('priceOverride', selectedLine)
          }
          break
        default:
          break
      }
    },
    [
      beginAdjustment,
      beginTender,
      cancelTender,
      cart,
      dispatch,
      quote.data,
      selectedLine,
      tendering,
    ],
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
    // they are safe to reserve. Withdrawn while tendering, for the same reason
    // ↑/↓ and Delete are.
    reservedKeys: {
      '+': () => {
        if (!tendering && cart.selectedKey !== null) {
          dispatch({ type: 'adjustQuantity', key: cart.selectedKey, delta: 1 })
        }
      },
      '-': () => {
        if (!tendering && cart.selectedKey !== null) {
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

      {recovery.status === 'unreachable' ? (
        <UnresolvedPaymentBanner onRetry={retryRecovery} />
      ) : null}

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
          onAdjust={(adjustment, line) => {
            void beginAdjustment(adjustment, line)
          }}
        />

        <div className="flex min-h-0 flex-col gap-3">
          {shift.isPending ? null : !shift.isSuccess ? (
            // A 404 from `/shifts/current` is the answer "no drawer is open".
            <OpenShiftPanel registerId={shift.registerId} currency={currency} />
          ) : tendering && quote.data !== undefined ? (
            <TenderPanel
              quote={quote.data}
              currency={currency}
              tenders={tenders}
              pending={pending}
              submitting={completeSale.isPending}
              onAdd={addTender}
              onRemove={(key) => {
                setTenders((current) => current.filter((tender) => tender.key !== key))
              }}
              onComplete={onCompleteSale}
              onCancel={cancelTender}
            />
          ) : (
            <>
              {/* The last sale's change, until the next item is scanned. It sits
                  above the total so the number being read out is the top of the
                  column, not something the eye has to hunt for. */}
              {completed !== null ? (
                <SaleCompletePanel
                  sale={completed.sale}
                  currency={currency}
                  provenance={completed.provenance}
                />
              ) : null}

              <TotalPanel
                cart={cart}
                /*
                 * Not `quote.data` directly.
                 *
                 * `useQuote` keeps the previous answer on screen while the next
                 * one is computed, so a scan does not blank the biggest number
                 * on the till. The cost is that an *emptied* cart inherits the
                 * last cart's total — and after a completed sale that put
                 * "€14.15" under the change due for a cart with nothing in it.
                 * A stale total that looks authoritative is the worst failure
                 * this screen has.
                 */
                quote={isEmpty(cart) ? undefined : quote.data}
                currency={currency}
                provisionalMinor={provisionalMinor}
                isQuoting={quote.isFetching}
                quoteFailed={quote.isError}
                pending={pending}
                canTender={!isEmpty(cart) && quote.data !== undefined && !quote.isError}
                onTender={beginTender}
              />
            </>
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

      {adjusting !== null ? (
        <LineAdjustDialog
          adjustment={adjusting.adjustment}
          line={adjusting.line}
          currency={currency}
          cartSubtotalMinor={provisionalMinor}
          authorizedByName={override.authorization?.authorizedByName ?? null}
          onApply={applyAdjustment}
          onCancel={() => {
            setAdjusting(null)
          }}
        />
      ) : null}
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
 * A payment this till cannot account for.
 *
 * The third answer, and the honest one: a sale was in flight when the page went
 * away, and the server cannot be reached to find out whether it was written.
 * **Saying "it failed" would be a guess that costs the customer a second
 * payment**, and saying "it succeeded" would lose one. So the till says what it
 * knows, tells the cashier not to re-ring it, and keeps the record so the
 * question can be put again the moment the connection is back.
 *
 * In the page and not dismissible by accident, for the same reason the unknown
 * code banner is in the page: a dialog here blocks the queue.
 */
function UnresolvedPaymentBanner({ onRetry }: { onRetry: () => void }) {
  return (
    <div
      role="alert"
      data-testid="unresolved-payment"
      className="flex flex-wrap items-center gap-3 rounded-lg border border-destructive/40 bg-destructive/10 px-4 py-2.5"
    >
      <p className="flex-1 text-sm text-foreground">
        <span className="font-medium">A payment was interrupted.</span> This till cannot reach the
        server to check whether it went through.{' '}
        <span className="font-medium">Do not ring it up again</span> until this is resolved.
      </p>
      <Button variant="outline" size="sm" onClick={onRetry}>
        Check again
      </Button>
    </div>
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
