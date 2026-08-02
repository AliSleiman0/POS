import type { ReactNode } from 'react'
import { Navigate, Outlet, useLocation } from 'react-router'
import { LoadingState } from '@/components/states'
import { useAuth } from './authContext'
import { ReauthOverlay } from './ReauthOverlay'
import type { Policy } from './policies'

/**
 * Requires a session.
 *
 * Note the asymmetry, which is the interesting part: `anonymous` navigates away
 * and `expired` does not. Losing a session you never had costs nothing; losing
 * one mid-transaction would take the screen's state with it. See
 * `ReauthOverlay`.
 */
export function RequireAuth() {
  const { status } = useAuth()
  const location = useLocation()

  if (status === 'loading') {
    return <LoadingState label="Restoring your session…" />
  }

  if (status === 'anonymous') {
    // `state.from` so the login page can return the user where they were going.
    return <Navigate to="/login" replace state={{ from: location.pathname + location.search }} />
  }

  return (
    <>
      <Outlet />
      {status === 'expired' ? <ReauthOverlay /> : null}
    </>
  )
}

/**
 * Requires a policy, for a whole route.
 *
 * Renders a plain refusal rather than redirecting: bouncing someone to the
 * dashboard for following a link leaves them wondering whether the click
 * registered.
 */
export function RequirePolicy({ policy, children }: { policy: Policy; children?: ReactNode }) {
  const { can } = useAuth()

  if (!can(policy)) {
    return (
      <div role="alert" className="flex flex-col items-center gap-2 py-16 text-center">
        <p className="text-sm font-medium text-foreground">You do not have access to this.</p>
        <p className="max-w-sm text-sm text-muted-foreground">
          Ask an owner or manager if you need it.
        </p>
      </div>
    )
  }

  return <>{children ?? <Outlet />}</>
}

/**
 * Shows its children only when the policy is granted.
 *
 * For controls rather than routes — a "New product" button a Cashier should not
 * see. Defence in depth only: the server refuses the call regardless, and a
 * field a role may not read is already absent from the payload.
 */
export function IfPolicy({
  policy,
  children,
  otherwise = null,
}: {
  policy: Policy
  children: ReactNode
  otherwise?: ReactNode
}) {
  const { can } = useAuth()
  return <>{can(policy) ? children : otherwise}</>
}
