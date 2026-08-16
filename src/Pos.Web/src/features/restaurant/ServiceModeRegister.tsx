import { LoadingState } from '@/components/states'
import { RegisterPage } from '@/features/register/RegisterPage'
import { FloorPage } from './FloorPage'
import { useServiceMode } from './serviceMode'

/**
 * What `/register` is, for this shop.
 *
 * **One route, two screens, rather than two routes.** Staff are trained on "the
 * register"; making a restaurant learn a different URL for the same button is
 * the wrong seam, and it would mean every deep link, every bookmark and the PIN
 * screen's return path all had to know the mode. The mode is a property of the
 * shop, so it belongs at the point of render.
 *
 * **The retail screen is wrapped, never edited.** `RegisterPage` is a thousand
 * lines that has paying users on it; the whole of the restaurant's divergence
 * from it lives on the other side of this branch.
 *
 * **Unknown renders nothing.** Not the retail till — see `useServiceMode`.
 * Guessing while the answer is in flight would flash the wrong screen on every
 * load in a restaurant, which is indistinguishable from the setting not having
 * saved.
 */
export function ServiceModeRegister() {
  const { mode } = useServiceMode()

  if (mode === null) {
    return <LoadingState label="Opening the till…" />
  }

  return mode === 'Restaurant' ? <FloorPage /> : <RegisterPage />
}
