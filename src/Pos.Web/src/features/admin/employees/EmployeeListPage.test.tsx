// First, before anything that imports the generated client — see fetchMock.ts.
import { installFetchHandler, requestUrl, resetFetchHandler } from '@/test/fetchMock'

import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { MemoryRouter } from 'react-router'
import { ToastProvider } from '@/components/toast'
import { AuthContext, type AuthContextValue } from '@/auth/authContext'
import { EmployeeListPage } from './EmployeeListPage'
import type { EmployeeSummary } from './queries'

const OWNER_ID = '11111111-1111-1111-1111-111111111111'
const CASHIER_ID = '22222222-2222-2222-2222-222222222222'
const FORMER_ID = '33333333-3333-3333-3333-333333333333'

const STAFF: EmployeeSummary[] = [
  {
    id: OWNER_ID,
    displayName: 'Pat Keeper',
    email: 'pat@example.com',
    role: 'Owner',
    isActive: true,
    hasPin: false,
    lastLoginAt: null,
  },
  {
    id: CASHIER_ID,
    displayName: 'Robin Vale',
    email: 'robin@example.com',
    role: 'Cashier',
    isActive: true,
    hasPin: true,
    lastLoginAt: null,
  },
  {
    id: FORMER_ID,
    displayName: 'Former Staff',
    email: 'gone@example.com',
    role: 'Cashier',
    isActive: false,
    hasPin: false,
    lastLoginAt: null,
  },
]

describe('EmployeeListPage', () => {
  let queryClient: QueryClient
  let deactivated: string[]

  beforeEach(() => {
    queryClient = new QueryClient({
      defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
    })

    deactivated = []

    installFetchHandler((input) => {
      const url = requestUrl(input)

      if (url.includes('/deactivate')) {
        deactivated.push(url)
        return Promise.resolve(new Response('', { status: 204 }))
      }

      if (url.includes('/api/v1/employees')) {
        const activeOnly = url.includes('activeOnly=true')

        return Promise.resolve(Response.json(activeOnly ? STAFF.filter((s) => s.isActive) : STAFF))
      }

      throw new Error(`Unexpected request: ${url}`)
    })
  })

  afterEach(() => {
    resetFetchHandler()
  })

  it('lists everybody, including staff who have left', async () => {
    renderPage(queryClient)

    await screen.findByText('Robin Vale')

    // Deactivated staff are shown by default. There is no reactivate route —
    // you bring somebody back by editing them — so hiding them here would make
    // the only way back invisible.
    expect(screen.getByText('Former Staff')).toBeInTheDocument()
    expect(screen.getByText('Deactivated')).toBeInTheDocument()
  })

  it('marks the signed-in user and offers them no deactivate button', async () => {
    renderPage(queryClient)

    await screen.findByText('Pat Keeper')

    expect(screen.getByText('you')).toBeInTheDocument()

    // The server refuses self-deactivation with a 409, so the button's only
    // possible outcome is an error. Its absence explains more than the error
    // would — and the server is still the gate (invariant 7).
    const ownerRow = rowFor('Pat Keeper')

    // Queried as a button, not as text: the status cell of a deactivated person
    // reads "Deactivated", which contains the button's own label as a substring.
    expect(within(ownerRow).queryByRole('button', { name: 'Deactivate' })).toBeNull()
  })

  it('offers no deactivate button for somebody already deactivated', async () => {
    renderPage(queryClient)

    await screen.findByText('Former Staff')

    const formerRow = rowFor('Former Staff')

    expect(within(formerRow).queryByRole('button', { name: 'Deactivate' })).toBeNull()
    expect(within(formerRow).getByText('Deactivated')).toBeInTheDocument()
  })

  it('takes two presses to deactivate somebody', async () => {
    const user = userEvent.setup()
    renderPage(queryClient)

    await screen.findByText('Robin Vale')

    const robinRow = rowFor('Robin Vale')

    // First press arms rather than fires. No browser `confirm()` anywhere —
    // it blocks the page, and a blocked till stalls a queue (invariant 10).
    await user.click(within(robinRow).getByRole('button', { name: 'Deactivate' }))
    expect(deactivated).toHaveLength(0)

    await user.click(within(robinRow).getByRole('button', { name: 'Really deactivate?' }))

    await waitFor(() => {
      expect(deactivated).toHaveLength(1)
    })

    expect(deactivated[0]).toContain(CASHIER_ID)
  })

  it('shows whether somebody can sign in at the till', async () => {
    renderPage(queryClient)

    await screen.findByText('Robin Vale')

    expect(
      within(rowFor('Robin Vale')).getByRole('button', { name: 'Reset PIN' }),
    ).toBeInTheDocument()
    expect(
      within(rowFor('Pat Keeper')).getByRole('button', { name: 'Set PIN' }),
    ).toBeInTheDocument()
  })
})

/** The row for one person, by the name in it. */
function rowFor(displayName: string): HTMLElement {
  const row = screen
    .getAllByTestId('employee-row')
    .find((candidate) => candidate.textContent?.includes(displayName))

  if (row === undefined) {
    throw new Error(`No row for ${displayName}.`)
  }

  return row
}

function renderPage(queryClient: QueryClient) {
  const auth: AuthContextValue = {
    status: 'authenticated',
    user: {
      id: OWNER_ID,
      displayName: 'Pat Keeper',
      role: 'Owner',
      policies: ['CanManageEmployees'],
    },
    tenant: null,
    policies: ['CanManageEmployees'],
    can: () => true,
    login: () => Promise.resolve(),
    pinLogin: () => Promise.resolve(),
    logout: () => Promise.resolve(),
    retrySession: () => undefined,
  }

  return render(
    <MemoryRouter>
      <QueryClientProvider client={queryClient}>
        <ToastProvider>
          <AuthContext value={auth}>
            <EmployeeListPage />
          </AuthContext>
        </ToastProvider>
      </QueryClientProvider>
    </MemoryRouter>,
  )
}
