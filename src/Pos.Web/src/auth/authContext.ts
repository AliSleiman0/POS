/**
 * The auth context and its hook.
 *
 * Separate from `AuthProvider.tsx` so that file exports a component and nothing
 * else — a module mixing components with other exports breaks Fast Refresh,
 * which on a POS means losing whatever is on screen every time the file is
 * touched.
 */

import { createContext, use } from 'react'
import type { components } from '@/api/schema'
import type { Policy } from './policies'

export type AuthUser = components['schemas']['AuthUser']
export type TenantSettings = components['schemas']['TenantSettings']
export type AuthResponse = components['schemas']['AuthResponse']

/**
 * `loading`   — a refresh token exists and the session is being restored.
 * `anonymous` — nobody is signed in. Route guards send you to /login.
 * `authenticated` — normal.
 * `expired`   — there *was* a session and it ended. Deliberately distinct from
 *               `anonymous`: the guards keep the route tree mounted and show a
 *               re-auth prompt over it, so an in-progress cart survives. See
 *               `ReauthOverlay`.
 * `unreachable` — the session could not be restored because nothing answered.
 *               Also deliberately distinct from `anonymous`: the credential was
 *               never rejected, so the tokens are kept and the guards offer a
 *               retry instead of a login form. A shop's broadband dropping for
 *               ten seconds must not sign a till out.
 */
export type SessionStatus = 'loading' | 'anonymous' | 'authenticated' | 'expired' | 'unreachable'

export interface AuthContextValue {
  status: SessionStatus
  user: AuthUser | null
  tenant: TenantSettings | null
  policies: readonly string[]
  can: (policy: Policy) => boolean
  login: (credentials: { tenantSlug: string; email: string; password: string }) => Promise<void>
  pinLogin: (credentials: { userId: string; pin: string }) => Promise<void>
  logout: () => Promise<void>
  /** Tries the restore again after an `unreachable`. */
  retrySession: () => void
}

export const AuthContext = createContext<AuthContextValue | null>(null)

export function useAuth(): AuthContextValue {
  const context = use(AuthContext)

  if (context === null) {
    throw new Error('useAuth must be used inside an <AuthProvider>')
  }

  return context
}
