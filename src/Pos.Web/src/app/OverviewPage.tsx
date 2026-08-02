import { Link } from 'react-router'
import { buttonVariants } from '@/components/ui/button'
import { useAuth } from '@/auth/authContext'
import { IfPolicy } from '@/auth/guards'

/**
 * The landing screen.
 *
 * Deliberately thin. Phase 5 puts the register here; Phase 6 adds reporting.
 * Until then it says who you are, what you may do, and where to go next —
 * which is more useful than an empty dashboard of zeroes.
 */
export function OverviewPage() {
  const { user, tenant, policies } = useAuth()

  return (
    <div className="mx-auto flex max-w-3xl flex-col gap-8 p-6">
      <header>
        <h1 className="text-2xl font-semibold text-foreground">{tenant?.name ?? 'Your shop'}</h1>
        <p className="mt-1 text-sm text-muted-foreground">
          Signed in as {user?.displayName} ({user?.role}). Prices are{' '}
          {tenant?.taxMode === 'Inclusive' ? 'tax inclusive' : 'tax exclusive'} in{' '}
          {tenant?.currencyCode}.
        </p>
      </header>

      <IfPolicy policy="CanSell">
        <section>
          {/* First, and biggest. For a cashier it is the only thing on this
              screen they will ever tap. */}
          <Link to="/register" className={buttonVariants({ size: 'lg' })}>
            Open the register
          </Link>
        </section>
      </IfPolicy>

      <IfPolicy policy="CanManageCatalog">
        <section className="flex flex-wrap gap-2">
          <Link to="/catalog" className={buttonVariants({ variant: 'outline', size: 'lg' })}>
            Manage catalog
          </Link>
          <Link to="/stock" className={buttonVariants({ variant: 'outline', size: 'lg' })}>
            Stock levels
          </Link>
          <IfPolicy policy="CanManageEmployees">
            <Link
              to="/settings/device"
              className={buttonVariants({ variant: 'outline', size: 'lg' })}
            >
              Set up this till
            </Link>
          </IfPolicy>
        </section>
      </IfPolicy>

      <section>
        <h2 className="text-sm font-medium text-foreground">What this account can do</h2>
        <ul className="mt-2 flex flex-wrap gap-1.5">
          {policies.map((policy) => (
            <li
              key={policy}
              className="rounded-full border border-border px-2.5 py-0.5 font-mono text-xs text-muted-foreground"
            >
              {policy}
            </li>
          ))}
        </ul>
        <p className="mt-2 text-xs text-muted-foreground">
          Granted by the server and re-checked on every call. Hiding a control here is a
          convenience, never the control itself.
        </p>
      </section>
    </div>
  )
}
