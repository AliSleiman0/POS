/**
 * How old the till's prices are, in the words a cashier would use.
 *
 * Its own file rather than a second export from `SyncStatus`, because a module
 * that exports both a component and a helper breaks fast refresh — and because
 * this is worth testing on its own. "Prices as of…" is a claim about how much
 * to trust what is on screen, and **getting it wrong in the reassuring
 * direction is the failure that matters**: a mirror silently a day old lets a
 * till sell last week's prices with complete confidence.
 */

/**
 * @param mirroredAt epoch ms of the last completed sync, or null if never.
 * @param offline whether the till can currently reach the server, which changes
 * what "no mirror" means: online it is a state that will fix itself, offline it
 * is a till that cannot sell.
 */
export function describeMirror(
  mirroredAt: number | null,
  offline: boolean,
  now: number = Date.now(),
): string {
  if (mirroredAt === null) {
    return offline ? 'No local prices' : 'Prices not yet stored'
  }

  const minutes = Math.floor((now - mirroredAt) / 60_000)

  if (minutes < 1) {
    return 'Prices up to date'
  }

  if (minutes < 60) {
    return `Prices as of ${String(minutes)} min ago`
  }

  const hours = Math.floor(minutes / 60)

  if (hours < 24) {
    return `Prices ${String(hours)}h old`
  }

  // Blunt past a day, deliberately. A mirror this old is one whose prices
  // nobody should be relying on without checking, and "1440 min ago" does not
  // say that to anybody.
  return `Prices ${String(Math.floor(hours / 24))} days old`
}
