// First, before anything that imports the generated client — see fetchMock.ts.
import { installFetchHandler, requestUrl, resetFetchHandler } from '@/test/fetchMock'

import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { ToastProvider } from '@/components/toast'
import { SettingsPage } from './SettingsPage'

/**
 * The settings screen, and the one rendering decision with money behind it.
 *
 * **Tax mode is read-only once a shop has traded.** Changing it reinterprets every
 * price already stored, so the server refuses it with a 409 — and this screen has to
 * say so *before* the click rather than offering a control that fails. Both halves of
 * that are pinned here, deterministically, because the state depends on whether a
 * database has ever recorded a sale and no UI test should be reading that from ambient
 * history. An earlier e2e did exactly that: it asserted "locked", passed locally
 * against an accumulated database, and failed on the runner's fresh one.
 */
describe('SettingsPage', () => {
  let queryClient: QueryClient
  let saved: unknown

  function installSettings(overrides: Record<string, unknown> = {}) {
    installFetchHandler(async (input, init) => {
      const url = requestUrl(input)

      if (!url.includes('/api/v1/settings')) {
        throw new Error(`Unexpected request: ${url}`)
      }

      const method = input instanceof Request ? input.method : (init?.method ?? 'GET')

      if (method === 'PUT') {
        // Read through the Request rather than off `init.body`. `openapi-fetch` may
        // hand the body over either way, and a `Request`'s body is a ReadableStream —
        // stringifying that yields "[object ReadableStream]" and the parse throws
        // inside the handler, which surfaces as a failed mutation rather than a
        // failed assertion.
        const body =
          input instanceof Request ? await input.clone().text() : String(init?.body ?? '{}')

        saved = JSON.parse(body)
      }

      return Response.json(settings(overrides))
    })
  }

  beforeEach(() => {
    queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
    })

    saved = undefined
  })

  afterEach(() => {
    resetFetchHandler()
  })

  it('renders tax mode read-only, with a reason, once the shop has traded', async () => {
    installSettings({ taxModeLocked: true })
    renderPage(queryClient)

    const taxMode = await screen.findByLabelText('Tax mode')

    expect(taxMode).toBeDisabled()

    // The reason, not just the disabled state. A greyed-out control with no
    // explanation reads as a bug, and somebody goes looking for the override.
    expect(screen.getByText(/recorded a sale/i)).toBeInTheDocument()
  })

  it('leaves tax mode editable before the first sale', async () => {
    installSettings({ taxModeLocked: false })
    renderPage(queryClient)

    const taxMode = await screen.findByLabelText('Tax mode')

    // The other half. A screen that disabled it unconditionally would be safe and
    // wrong: a shop that has not opened yet is exactly who needs to set this.
    expect(taxMode).toBeEnabled()
    expect(screen.queryByText(/recorded a sale/i)).toBeNull()
  })

  it('seeds every field from the server rather than from a default', async () => {
    installSettings()
    renderPage(queryClient)

    // `/^Name/`, not `'Name'`. `Field` appends a visible asterisk to a required
    // label, so the accessible name is "Name*" and an exact match finds nothing.
    expect(await screen.findByLabelText(/^Name/)).toHaveValue('Corner Shop')
    expect(screen.getByLabelText('Footer')).toHaveValue('Thanks for shopping')
    expect(screen.getByLabelText('Cash rounding')).toHaveValue('0.05')
  })

  it('shows currency and time zone without letting them be changed', async () => {
    installSettings()
    renderPage(queryClient)

    // Not oversights. Changing a zone moves every trading-day boundary that has
    // already been reported on, so yesterday's Z-report stops matching yesterday.
    expect(await screen.findByLabelText('Currency')).toBeDisabled()
    expect(screen.getByLabelText('Time zone')).toBeDisabled()
  })

  it('sends the whole object, with blanks as null', async () => {
    const user = userEvent.setup()
    installSettings()
    renderPage(queryClient)

    const taxNumber = await screen.findByLabelText('Tax number')
    await user.clear(taxNumber)

    await user.click(screen.getByRole('button', { name: 'Save settings' }))

    await waitFor(() => {
      expect(saved).toBeDefined()
    })

    // A PUT takes the whole object, so a cleared field has to arrive as an explicit
    // null — an omitted key would be ambiguous between "unchanged" and "cleared".
    expect(saved).toMatchObject({ name: 'Corner Shop', taxNumber: null })
  })

  it('refuses to submit a rounding increment the server would reject', async () => {
    const user = userEvent.setup()
    installSettings()
    renderPage(queryClient)

    const rounding = await screen.findByLabelText('Cash rounding')
    await user.clear(rounding)

    // 5 rounds every cash total to the nearest fiver. It is a typo for 0.05 rather
    // than a rule anybody has, and the server refuses it too.
    await user.type(rounding, '5')
    await user.click(screen.getByRole('button', { name: 'Save settings' }))

    expect(await screen.findByRole('alert')).toHaveTextContent(/between 0 and 1/i)
    expect(saved).toBeUndefined()
  })
})

function settings(overrides: Record<string, unknown> = {}) {
  return {
    name: 'Corner Shop',
    slug: 'corner-shop',
    currencyCode: 'EUR',
    timeZoneId: 'Europe/Dublin',
    taxMode: 'Inclusive',
    taxModeLocked: false,
    cashRoundingIncrement: 0.05,
    businessDayStartOffset: '04:00:00',
    addressLine: '14 Harbour Road',
    taxNumber: 'IE1234567FA',
    receiptHeader: 'Open 7 days',
    receiptFooter: 'Thanks for shopping',
    ...overrides,
  }
}

function renderPage(queryClient: QueryClient) {
  return render(
    <QueryClientProvider client={queryClient}>
      <ToastProvider>
        <SettingsPage />
      </ToastProvider>
    </QueryClientProvider>,
  )
}
