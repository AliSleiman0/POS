import { NavLink, Outlet } from 'react-router'
import { useQuery } from '@tanstack/react-query'
import { Button } from '@/components/ui/button'
import { ErrorBoundary } from './ErrorBoundary'
import { useAuth } from '@/auth/authContext'
import { IfPolicy } from '@/auth/guards'
import { CartProvider } from '@/features/register/CartProvider'
import { OverrideProvider } from '@/features/register/OverrideProvider'
import { useCurrentShift } from '@/features/register/queries'
import { clearCart } from '@/features/register/storage'
import { formatMoney } from '@/lib/money'
import { cn } from '@/lib/utils'

/**
 * The authenticated chrome: navigation, who is signed in, and the drawer state.
 *
 * Hosts the `CartProvider`, deliberately. The cart has to outlive a route change
 * — a cashier checking a price in the catalog must come back to the same six
 * items — and it has to outlive a session expiring, which `RequireAuth` handles
 * by rendering a prompt *over* this tree rather than navigating away from it. A
 * cart inside the register screen would be thrown away by both.
 *
 * `OverrideProvider` sits inside it and holds any manager authorisation for the
 * same cart. Beside the cart rather than in it, so that the cart's
 * `sessionStorage` persistence cannot write a live credential to disk as a side
 * effect — which it now genuinely would, since the cart is persisted on every
 * change.
 */
export function AppLayout() {
  const { user, tenant, logout } = useAuth()

  return (
    <CartProvider>
      <OverrideProvider>
        <div className="flex h-full flex-col">
          <header className="flex items-center gap-6 border-b border-border px-4 py-2">
            <div className="flex min-w-0 flex-col">
              <span className="truncate text-sm font-semibold text-foreground">
                {tenant?.name ?? 'POS'}
              </span>
              <span className="truncate text-xs text-muted-foreground">{tenant?.slug}</span>
            </div>

            <nav className="flex flex-1 items-center gap-1">
              <NavItem to="/">Overview</NavItem>
              <IfPolicy policy="CanSell">
                <NavItem to="/register">Register</NavItem>
              </IfPolicy>
              <IfPolicy policy="CanManageCatalog">
                <NavItem to="/catalog">Catalog</NavItem>
                <NavItem to="/catalog/categories">Categories</NavItem>
                <NavItem to="/catalog/tax-classes">Tax</NavItem>
                <NavItem to="/stock">Stock</NavItem>
              </IfPolicy>
              {/* A third group: reconciliation is CanCloseShift, which a Cashier
                  does not hold and a Manager does. */}
              <IfPolicy policy="CanCloseShift">
                <NavItem to="/reports/daily">Reports</NavItem>
              </IfPolicy>
            </nav>

            <ShiftIndicator />

            <div className="flex items-center gap-3">
              <div className="flex flex-col items-end">
                <span className="text-sm font-medium text-foreground">{user?.displayName}</span>
                <span className="text-xs text-muted-foreground">{user?.role}</span>
              </div>
              <Button
                variant="outline"
                size="sm"
                onClick={() => {
                  // The basket does not follow a sign-out. It survives a route
                  // change and a session expiring — both are the same person
                  // still serving the same customer — but signing out at a
                  // shared till means the next person, and handing them the
                  // last one's cart is how the wrong items get sold.
                  clearCart()
                  void logout()
                }}
              >
                Sign out
              </Button>
            </div>
          </header>

          {/* Inside the layout, so a crash on one screen leaves the navigation
          usable instead of blanking the whole application.

          No padding here: the register runs edge to edge, and every other page
          brings its own. A layout that special-cased one route's padding would
          have to know which route it was rendering. */}
          <main className="min-h-0 flex-1 overflow-y-auto">
            <ErrorBoundary>
              <Outlet />
            </ErrorBoundary>
          </main>

          <ApiFooter />
        </div>
      </OverrideProvider>
    </CartProvider>
  )
}

function NavItem({ to, children }: { to: string; children: string }) {
  return (
    <NavLink
      to={to}
      // `end` so "Overview" is not highlighted on every nested catalog route.
      end={to === '/' || to === '/catalog'}
      className={({ isActive }) =>
        cn(
          'rounded-lg px-3 py-1.5 text-sm font-medium transition-colors',
          isActive ? 'bg-muted text-foreground' : 'text-muted-foreground hover:text-foreground',
        )
      }
    >
      {children}
    </NavLink>
  )
}

/**
 * Whether this till has an open drawer.
 *
 * A sale needs an open shift, so this is the first thing anyone standing at a
 * register needs to know. `GET /shifts/current` answers 404 when there is none,
 * which `unwrap` turns into a `ProblemError` — hence `isError` meaning "no
 * shift" rather than "broken".
 *
 * Shares `useCurrentShift` with the register screen, so opening a drawer there
 * updates this without a second query key to keep in step.
 */
function ShiftIndicator() {
  const { tenant } = useAuth()
  const shift = useCurrentShift()

  if (shift.registerId === null) {
    return <span className="text-xs text-muted-foreground">Not a register</span>
  }

  if (shift.isPending) {
    return <span className="text-xs text-muted-foreground">Checking drawer…</span>
  }

  // 404 is how the API says "no shift is open" — see docs/API.md.
  if (shift.isError) {
    return <span className="text-xs text-muted-foreground">Drawer closed</span>
  }

  return (
    <span className="text-xs font-medium text-foreground">
      Drawer open · float {formatMoney(shift.data.openingFloat, tenant?.currencyCode ?? 'GBP')}
    </span>
  )
}

/**
 * The API reachability indicator, carried over from the 0.5 scaffold.
 *
 * Kept because the dev proxy pointing at the wrong port is a real failure mode
 * that had gone unnoticed for three phases, and this is the one thing on screen
 * that makes it obvious rather than mysterious.
 */
function ApiFooter() {
  const health = useQuery({
    queryKey: ['health'],
    queryFn: async () => {
      const response = await fetch('/health/ready')
      if (!response.ok) {
        throw new Error(`HTTP ${String(response.status)}`)
      }
      return (await response.text()).trim()
    },
    retry: false,
    staleTime: 30_000,
  })

  return (
    <footer className="flex items-center justify-end gap-2 border-t border-border px-4 py-1.5">
      <span className="text-xs text-muted-foreground">API</span>
      <span
        data-testid="api-health"
        className={cn(
          'rounded-full px-2 py-0.5 text-xs font-medium',
          health.isPending && 'bg-muted text-muted-foreground',
          health.isError && 'bg-destructive/10 text-destructive',
          health.isSuccess && 'bg-muted text-foreground',
        )}
      >
        {health.isPending
          ? 'checking…'
          : health.isError
            ? 'unreachable'
            : (health.data ?? 'Healthy')}
      </span>
    </footer>
  )
}
