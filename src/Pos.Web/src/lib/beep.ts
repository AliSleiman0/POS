/**
 * Scan feedback you can hear.
 *
 * **Staff do not look at the screen between items.** They look at the goods and
 * at the customer, and the only thing telling them the last item registered is
 * the sound. A till with no audible feedback is one where a missed scan is found
 * at the total, with a queue waiting, by recounting the basket.
 *
 * Synthesised rather than shipped as an asset: two oscillator tones need no
 * file, no fetch, no cache entry and no decision about licensing a beep.
 *
 * Every failure here is swallowed. Autoplay policy, a device with no audio, a
 * browser that has never heard of `AudioContext` — none of that may interfere
 * with the scan itself, which is the part that matters. The visual flash on the
 * cart line covers the same ground for anyone who has muted the till or cannot
 * hear it.
 */

const MUTED_KEY = 'pos.scanSoundMuted'

/** One context for the app: browsers cap how many a page may create. */
let context: AudioContext | null = null

export type ScanTone = 'ok' | 'miss'

/** Recognisable apart without being listened for: a chirp up, or a low buzz. */
const TONES: Record<ScanTone, { frequency: number; durationMs: number; gain: number }> = {
  ok: { frequency: 1320, durationMs: 70, gain: 0.06 },
  miss: { frequency: 220, durationMs: 240, gain: 0.08 },
}

export function isScanSoundMuted(): boolean {
  try {
    return localStorage.getItem(MUTED_KEY) === 'true'
  } catch {
    // Storage disabled. Audible is the useful default.
    return false
  }
}

export function setScanSoundMuted(muted: boolean): void {
  try {
    localStorage.setItem(MUTED_KEY, String(muted))
  } catch {
    // The preference does not survive the session. Nothing else breaks.
  }
}

/**
 * Plays one tone. Never throws, never blocks the caller.
 */
export function beep(tone: ScanTone): void {
  if (isScanSoundMuted()) {
    return
  }

  try {
    if (typeof AudioContext === 'undefined') {
      return
    }

    context ??= new AudioContext()

    // Created suspended when the page has had no interaction yet. A keystroke
    // is an interaction, so by the time a scan lands this resolves.
    if (context.state === 'suspended') {
      void context.resume()
    }

    const { frequency, durationMs, gain } = TONES[tone]
    const oscillator = context.createOscillator()
    const envelope = context.createGain()

    oscillator.frequency.value = frequency
    oscillator.type = tone === 'ok' ? 'sine' : 'square'

    // Ramped rather than switched off: an abruptly stopped oscillator clicks,
    // and a click several hundred times a day is what makes staff mute a till.
    const now = context.currentTime
    const endsAt = now + durationMs / 1000

    envelope.gain.setValueAtTime(gain, now)
    envelope.gain.exponentialRampToValueAtTime(0.0001, endsAt)

    oscillator.connect(envelope).connect(context.destination)
    oscillator.start(now)
    oscillator.stop(endsAt)
  } catch {
    // See the module comment: feedback is a courtesy, the scan is the product.
  }
}
