import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { MemoryRouter, Route, Routes } from 'react-router'
import { AuthContext, type AuthContextValue } from './authContext'
import { IfPolicy, RequireAuth, RequirePolicy } from './guards'
import { hasPolicy, type Policy } from './policies'

/**
 * A session in a given state, without going near the network.
 *
 * The policies are the ones `GET /auth/me` returns for that role — see
 * `PolicyCatalog.PoliciesFor`.
 */
function session(overrides: Partial<AuthContextValue>): AuthContextValue {
  const policies = overrides.policies ?? []

  return {
    status: 'authenticated',
    user: { id: 'u', displayName: 'Robin Vale', role: 'Cashier', policies: [...policies] },
    tenant: {
      slug: 'corner-shop',
      name: 'Corner Shop',
      currencyCode: 'GBP',
      timeZoneId: 'Europe/London',
      taxMode: 'Inclusive',
    },
    policies,
    can: (policy: Policy) => hasPolicy(policies, policy),
    login: async () => undefined,
    pinLogin: async () => undefined,
    logout: async () => undefined,
    ...overrides,
  }
}

function renderWith(value: AuthContextValue, initialPath = '/catalog') {
  return render(
    <AuthContext value={value}>
      <MemoryRouter initialEntries={[initialPath]}>
        <Routes>
          <Route path="/login" element={<p>Sign in</p>} />
          <Route element={<RequireAuth />}>
            <Route path="/catalog" element={<p>Catalog screen</p>} />
          </Route>
        </Routes>
      </MemoryRouter>
    </AuthContext>,
  )
}

const CASHIER = ['CanSell']
const MANAGER = [
  'CanApplyDiscount',
  'CanCloseShift',
  'CanManageCatalog',
  'CanOverridePrice',
  'CanRefund',
  'CanSell',
  'CanVoidSale',
]
const OWNER = [...MANAGER, 'CanManageEmployees', 'CanViewMargins']

describe('RequireAuth', () => {
  it('waits rather than bouncing while a session is being restored', () => {
    renderWith(session({ status: 'loading' }))

    // Redirecting here would sign out anyone who reloaded the page, because the
    // refresh has not finished yet.
    expect(screen.getByRole('status')).toBeInTheDocument()
    expect(screen.queryByText('Sign in')).not.toBeInTheDocument()
  })

  it('sends an anonymous visitor to the login page', () => {
    renderWith(session({ status: 'anonymous' }))

    expect(screen.getByText('Sign in')).toBeInTheDocument()
    expect(screen.queryByText('Catalog screen')).not.toBeInTheDocument()
  })

  it('renders the screen for a live session', () => {
    renderWith(session({ status: 'authenticated', policies: CASHIER }))

    expect(screen.getByText('Catalog screen')).toBeInTheDocument()
  })

  it('keeps the screen mounted when the session expires, and prompts over it', () => {
    // The asymmetry that Phase 5 depends on. A redirect here would unmount the
    // route tree and take a half-built cart with it — which is the kind of
    // thing that gets a POS replaced. The screen must still be there.
    renderWith(session({ status: 'expired', policies: CASHIER }))

    expect(screen.getByText('Catalog screen')).toBeInTheDocument()
    expect(screen.getByRole('dialog')).toBeInTheDocument()
    expect(screen.getByText('Session expired')).toBeInTheDocument()
    expect(screen.queryByText('Sign in')).not.toBeInTheDocument()
  })
})

describe('RequirePolicy', () => {
  function renderPolicy(policies: string[], policy: Policy) {
    return render(
      <AuthContext value={session({ policies })}>
        <RequirePolicy policy={policy}>
          <p>Protected</p>
        </RequirePolicy>
      </AuthContext>,
    )
  }

  it('refuses in place rather than redirecting', () => {
    renderPolicy(CASHIER, 'CanManageCatalog')

    // Bouncing to the dashboard leaves someone who followed a link wondering
    // whether their click registered at all.
    expect(screen.queryByText('Protected')).not.toBeInTheDocument()
    expect(screen.getByRole('alert')).toHaveTextContent('do not have access')
  })

  it('admits a role that holds the policy', () => {
    renderPolicy(MANAGER, 'CanManageCatalog')

    expect(screen.getByText('Protected')).toBeInTheDocument()
  })
})

describe('IfPolicy', () => {
  function renderControl(policies: string[], policy: Policy) {
    return render(
      <AuthContext value={session({ policies })}>
        <IfPolicy policy={policy} otherwise={<p>Hidden</p>}>
          <button type="button">New product</button>
        </IfPolicy>
      </AuthContext>,
    )
  }

  it('hides a control the role does not hold', () => {
    renderControl(CASHIER, 'CanManageCatalog')

    expect(screen.queryByRole('button', { name: 'New product' })).not.toBeInTheDocument()
    expect(screen.getByText('Hidden')).toBeInTheDocument()
  })

  it('shows it to a manager', () => {
    renderControl(MANAGER, 'CanManageCatalog')

    expect(screen.getByRole('button', { name: 'New product' })).toBeInTheDocument()
  })

  it('keeps margins to the owner', () => {
    // CanViewMargins is Owner-only: a manager who can see cost prices can
    // price-shop the shop's suppliers. This gate is defence in depth — the
    // server omits the column from a manager's response entirely.
    renderControl(MANAGER, 'CanViewMargins')
    expect(screen.queryByRole('button', { name: 'New product' })).not.toBeInTheDocument()

    renderControl(OWNER, 'CanViewMargins')
    expect(screen.getByRole('button', { name: 'New product' })).toBeInTheDocument()
  })
})
