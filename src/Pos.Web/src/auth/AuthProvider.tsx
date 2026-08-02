/**
 * The session: who is signed in, what they may do, and which shop they are in.
 */

import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { api, deviceApi, unwrap } from '@/api/client'
import { refreshSession } from './refresh'
import {
  accessTokenExpiresAt,
  clearTokens,
  getRefreshToken,
  onTokensCleared,
  setTokens,
} from './tokenStore'
import { hasPolicy, type Policy } from './policies'
import {
  AuthContext,
  type AuthContextValue,
  type AuthResponse,
  type SessionStatus,
} from './authContext'

/** Refresh this long before expiry. Matches `tokenStore`'s own margin. */
const REFRESH_MARGIN_MS = 60_000

export function AuthProvider({ children }: { children: ReactNode }) {
  const queryClient = useQueryClient()

  const [status, setStatus] = useState<SessionStatus>(() =>
    getRefreshToken() !== null ? 'loading' : 'anonymous',
  )

  // Read inside the `onTokensCleared` listener, which is registered once and
  // would otherwise close over the status it was registered with.
  const statusRef = useRef(status)
  statusRef.current = status

  const enabled = status === 'loading' || status === 'authenticated' || status === 'expired'

  const me = useQuery({
    queryKey: ['auth', 'me'],
    queryFn: () => unwrap(api.GET('/api/v1/auth/me')),
    enabled,
    // The session is not something to be stale about, but it also does not
    // change on its own — refetching is driven by login/logout, not a timer.
    staleTime: Number.POSITIVE_INFINITY,
    retry: false,
  })

  // A `me` that fails despite a token means the token is not usable. The client
  // middleware has already tried to refresh by this point.
  useEffect(() => {
    if (me.isError && statusRef.current === 'loading') {
      clearTokens()
    }
  }, [me.isError])

  useEffect(() => {
    if (me.isSuccess && statusRef.current !== 'authenticated') {
      setStatus('authenticated')
    }
  }, [me.isSuccess])

  useEffect(
    () =>
      onTokensCleared(() => {
        // Only a *live* session becoming expired warrants the overlay. Failing
        // to restore one on page load is simply "not signed in".
        setStatus(statusRef.current === 'authenticated' ? 'expired' : 'anonymous')
      }),
    [],
  )

  /**
   * Silent refresh, scheduled from the token's own expiry.
   *
   * The client middleware also refreshes reactively, so this is not the only
   * defence — it exists so an idle till that has not made a request for twenty
   * minutes is still holding a usable token when the next customer arrives,
   * rather than paying a refresh round trip in front of them.
   */
  useEffect(() => {
    if (status !== 'authenticated') {
      return
    }

    const expiresAt = accessTokenExpiresAt()

    if (expiresAt === null) {
      return
    }

    const delay = Math.max(0, expiresAt - REFRESH_MARGIN_MS - Date.now())
    const timer = setTimeout(() => {
      void refreshSession()
    }, delay)

    return () => {
      clearTimeout(timer)
    }
    // `me.dataUpdatedAt` is not the trigger; a completed refresh replaces the
    // token, and the effect re-runs because `status` and the tick below change.
  }, [status, me.dataUpdatedAt])

  const adoptSession = useCallback(
    async (tokens: AuthResponse) => {
      // `expiresIn` is an int32, and .NET's OpenAPI describes every numeric as
      // `number | string` — so the generated type carries the union even though
      // the wire format is always a number. Resolved here rather than trusting
      // it, because `Date.now() + '900' * 1000` is NaN and the session would
      // then look permanently expired.
      setTokens({ ...tokens, expiresIn: Number(tokens.expiresIn) })
      // Drop everything the previous user could see before the next screen
      // paints. A PIN swap changes who is looking at the till, and a Cashier
      // must not inherit an Owner's cached cost prices.
      queryClient.clear()
      setStatus('authenticated')
      await queryClient.invalidateQueries({ queryKey: ['auth', 'me'] })
    },
    [queryClient],
  )

  const login = useCallback<AuthContextValue['login']>(
    async (credentials) => {
      const response = await unwrap(api.POST('/api/v1/auth/login', { body: credentials }))
      await adoptSession(response)
    },
    [adoptSession],
  )

  const pinLogin = useCallback<AuthContextValue['pinLogin']>(
    async (credentials) => {
      // The device-token client: `POST /auth/pin` is authenticated by the
      // DeviceToken *scheme*, so an unenrolled till is turned away before the
      // PIN is read. See `deviceApi` in api/client.ts.
      const response = await unwrap(deviceApi.POST('/api/v1/auth/pin', { body: credentials }))
      await adoptSession(response)
    },
    [adoptSession],
  )

  const logout = useCallback(async () => {
    const refreshToken = getRefreshToken()

    if (refreshToken !== null) {
      try {
        // Revokes the whole refresh-token family server-side. Best-effort: a
        // logout that reported failure is a logout users retry, and the local
        // session is dropped either way.
        await unwrap(api.POST('/api/v1/auth/logout', { body: { refreshToken } }))
      } catch {
        // Intentionally swallowed — see above.
      }
    }

    clearTokens()
    setStatus('anonymous')
    queryClient.clear()
  }, [queryClient])

  // `?? []` allocates a fresh array on every render, so this must be memoised
  // or the context value below is new every time — which re-renders every
  // screen reading it, including the register, on every unrelated state change.
  const policies = useMemo(() => me.data?.user.policies ?? [], [me.data])

  const value = useMemo<AuthContextValue>(
    () => ({
      status,
      user: me.data?.user ?? null,
      tenant: me.data?.tenant ?? null,
      policies,
      can: (policy: Policy) => hasPolicy(policies, policy),
      login,
      pinLogin,
      logout,
    }),
    [status, me.data, policies, login, pinLogin, logout],
  )

  return <AuthContext value={value}>{children}</AuthContext>
}
