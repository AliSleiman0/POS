/**
 * What the till does about a payment it was taking when the page went away.
 *
 * A reload during `POST /sales` leaves the register holding the GUID it
 * submitted and nothing else. Two things could have happened, and they need
 * opposite actions from the cashier:
 *
 * - the sale was written — read the change out, do not take the money again;
 * - the request never arrived — take the payment again.
 *
 * Guessing either way is how a customer is charged twice, so this asks:
 * `GET /sales/by-client-transaction/{id}` is a read, and a page load is not a
 * person pressing Complete. **A third outcome is that the question cannot be
 * put** — offline, server down — and that is reported as itself rather than
 * collapsed into one of the two answers.
 */

import { useCallback, useEffect, useRef, useState } from 'react'
import type { components } from '@/api/schema'
import { fetchSaleByKey } from './queries'
import { clearSaleInFlight, readSaleInFlight, type SaleInFlight } from './storage'

type SaleResponse = components['schemas']['SaleResponse']

export type Recovery =
  /** Nothing was in flight, or the question has been answered. */
  | { status: 'settled' }
  /** Asking the server what the key bought. */
  | { status: 'checking' }
  /**
   * The server could not be reached, so the till does not know. The banner this
   * drives is the only honest thing to show, and it says: do not re-ring it.
   */
  | { status: 'unreachable' }

export function useSaleRecovery({
  enabled,
  registerId,
  onTaken,
  onNotTaken,
}: {
  /** Only ask once there is a session to ask with. */
  enabled: boolean
  /** This device's till, from enrolment. */
  registerId: string | null
  /** The sale was written. The till never saw this answer the first time. */
  onTaken: (sale: SaleResponse) => void
  /** Nothing was charged. The tenders come back so one press finishes it. */
  onNotTaken: (record: SaleInFlight) => void
}): { recovery: Recovery; retry: () => void } {
  const [recovery, setRecovery] = useState<Recovery>({ status: 'settled' })

  // The callbacks are read through a ref so this does not re-run when the page
  // re-renders with new closures — which is every keystroke.
  const handlers = useRef({ onTaken, onNotTaken })
  handlers.current = { onTaken, onNotTaken }

  /** Set once the question has been put and answered, so it is asked once. */
  const resolved = useRef(false)

  const resolve = useCallback(async () => {
    const record = readSaleInFlight()

    if (record === null) {
      setRecovery({ status: 'settled' })
      return
    }

    /*
     * Somebody else's interrupted sale.
     *
     * A tab restored on a different till, or a device re-enrolled since. The
     * key belongs to a register this one is not, and adopting it would put
     * another drawer's sale on this screen. Dropped rather than resolved.
     */
    if (registerId === null || record.registerId !== registerId) {
      clearSaleInFlight()
      setRecovery({ status: 'settled' })
      return
    }

    setRecovery({ status: 'checking' })

    try {
      const sale = await fetchSaleByKey(record.saleKey)

      // Answered either way, so the record has done its job. What replaces it
      // is either a completed sale on screen or a restored tender pad, and both
      // write a fresh record if they submit again.
      clearSaleInFlight()
      setRecovery({ status: 'settled' })

      if (sale === null) {
        handlers.current.onNotTaken(record)
      } else {
        handlers.current.onTaken(sale)
      }
    } catch {
      // **The record is kept.** It is the only thing that can answer this later,
      // and throwing it away because one request failed would leave the till
      // permanently unable to find out.
      setRecovery({ status: 'unreachable' })
    }
  }, [registerId])

  useEffect(() => {
    if (!enabled || resolved.current) {
      return
    }

    resolved.current = true
    void resolve()
  }, [enabled, resolve])

  const retry = useCallback(() => {
    void resolve()
  }, [resolve])

  return { recovery, retry }
}
