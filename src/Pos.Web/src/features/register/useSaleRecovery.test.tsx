// First, before anything that imports the generated client — see fetchMock.ts.
import { installFetchHandler, requestUrl, resetFetchHandler } from '@/test/fetchMock'

import { renderHook, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { readSaleInFlight, writeSaleInFlight } from './storage'
import { useSaleRecovery } from './useSaleRecovery'

const REGISTER_ID = 'r-till-1'

/**
 * Resolving a payment the till lost the answer to.
 *
 * There are **three** outcomes, not two, and the third is the one worth writing
 * a test for: the question cannot be put at all. A till that collapsed "I cannot
 * reach the server" into either real answer would tell a cashier to re-ring a
 * sale that was already charged, or to hand over goods that were not.
 */
describe('useSaleRecovery', () => {
  const record = {
    saleKey: '11111111-2222-3333-4444-555555555555',
    tenders: [{ key: 't1', amountMinor: 500 }],
    registerId: REGISTER_ID,
    shiftId: 's-1',
    at: 1_760_000_000_000,
  }

  beforeEach(() => {
    sessionStorage.clear()
  })

  afterEach(() => {
    resetFetchHandler()
    sessionStorage.clear()
  })

  function run(overrides: { registerId?: string | null } = {}) {
    const onTaken = vi.fn()
    const onNotTaken = vi.fn()

    const view = renderHook(() =>
      useSaleRecovery({
        enabled: true,
        registerId: overrides.registerId === undefined ? REGISTER_ID : overrides.registerId,
        onTaken,
        onNotTaken,
      }),
    )

    return { ...view, onTaken, onNotTaken }
  }

  it('asks nothing when no payment was interrupted', async () => {
    installFetchHandler(() => {
      throw new Error('The till asked about a sale it never submitted.')
    })

    const { result, onTaken, onNotTaken } = run()

    await waitFor(() => {
      expect(result.current.recovery.status).toBe('settled')
    })

    expect(onTaken).not.toHaveBeenCalled()
    expect(onNotTaken).not.toHaveBeenCalled()
  })

  it('reports the sale when the key bought one', async () => {
    writeSaleInFlight(record)

    installFetchHandler((input) => {
      expect(requestUrl(input)).toContain(`/sales/by-client-transaction/${record.saleKey}`)

      return Promise.resolve(Response.json({ id: 'sale-1', saleNumber: 7, changeGiven: 3.8 }))
    })

    const { onTaken, onNotTaken } = run()

    await waitFor(() => {
      expect(onTaken).toHaveBeenCalledOnce()
    })

    expect(onNotTaken).not.toHaveBeenCalled()

    // Answered, so the record has done its job — and leaving it would make the
    // next load ask about a sale that is already on the screen.
    expect(readSaleInFlight()).toBeNull()
  })

  it('reports that nothing was charged on a 404, with the amounts intact', async () => {
    writeSaleInFlight(record)

    installFetchHandler(() => Promise.resolve(new Response(null, { status: 404 })))

    const { onNotTaken } = run()

    await waitFor(() => {
      expect(onNotTaken).toHaveBeenCalledOnce()
    })

    // The tenders come back so finishing is one press rather than a recount.
    expect(onNotTaken.mock.calls[0]?.[0]).toMatchObject({ tenders: record.tenders })
    expect(readSaleInFlight()).toBeNull()
  })

  it('says it does not know when the server cannot be reached, and keeps the record', async () => {
    /*
     * The assertion this file exists for.
     *
     * Neither callback may fire. Calling `onNotTaken` here would restore a
     * tender pad for a sale that might already be paid for, and one press would
     * charge the customer twice; calling `onTaken` would need a sale nobody has.
     * The banner this drives says "do not ring it up again", which is the only
     * true thing available.
     */
    writeSaleInFlight(record)

    installFetchHandler(() => Promise.reject(new TypeError('Failed to fetch')))

    const { result, onTaken, onNotTaken } = run()

    await waitFor(() => {
      expect(result.current.recovery.status).toBe('unreachable')
    })

    expect(onTaken).not.toHaveBeenCalled()
    expect(onNotTaken).not.toHaveBeenCalled()

    // Kept, because it is the only thing that can answer this later. Dropping it
    // on one failed request would leave the till permanently unable to find out.
    expect(readSaleInFlight()).not.toBeNull()
  })

  it('retries the question and settles once it can be answered', async () => {
    writeSaleInFlight(record)

    let reachable = false

    installFetchHandler(() =>
      reachable
        ? Promise.resolve(new Response(null, { status: 404 }))
        : Promise.reject(new TypeError('Failed to fetch')),
    )

    const { result, onNotTaken } = run()

    await waitFor(() => {
      expect(result.current.recovery.status).toBe('unreachable')
    })

    reachable = true
    result.current.retry()

    await waitFor(() => {
      expect(onNotTaken).toHaveBeenCalledOnce()
    })

    expect(result.current.recovery.status).toBe('settled')
  })

  it('does not adopt a sale belonging to another till', async () => {
    /*
     * A tab restored on a different device, or one re-enrolled since. Showing
     * another drawer's sale here would put a payment on a till that never took
     * it, and the two would not reconcile at cash-up.
     */
    writeSaleInFlight({ ...record, registerId: 'r-some-other-till' })

    installFetchHandler(() => {
      throw new Error('The till asked about another register’s sale.')
    })

    const { result, onTaken, onNotTaken } = run()

    await waitFor(() => {
      expect(result.current.recovery.status).toBe('settled')
    })

    expect(onTaken).not.toHaveBeenCalled()
    expect(onNotTaken).not.toHaveBeenCalled()
    expect(readSaleInFlight()).toBeNull()
  })
})
