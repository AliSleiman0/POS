import { expect, test } from '@playwright/test'
import { readFileSync } from 'node:fs'
import { join } from 'node:path'

/**
 * What the built service worker actually does.
 *
 * **Asserted against the emitted `dist/sw.js`, not against `vite.config.ts`.**
 * The config is an input to Workbox's generator, and the two decisions that
 * matter here — never taking over mid-sale, never caching an API response —
 * are properties of the *output*. A test that read the config back would pass
 * on the day a plugin upgrade changed what an option means.
 *
 * These ran as a `test.describe` in the e2e project because that is where Node
 * globals live (`tsconfig.app.json` deliberately keeps `node:fs` out of `src`),
 * and because a build artefact is exactly what an end-to-end check is for.
 * They need `pnpm build` to have run; each skips with a clear reason otherwise,
 * rather than failing for the wrong cause.
 */

const DIST = join(import.meta.dirname, '..', 'dist')

/**
 * The built file, or `null` when there is no build to read.
 *
 * **Under CI it throws instead**, and that is the important half. Every
 * assertion in this file is `test.skip(...)`-guarded on a missing `dist`, which
 * is a kindness locally — running one spec without building first should say so
 * rather than fail for the wrong reason. In CI it would be the opposite: five
 * green skips reading exactly like five green tests, for rules about a service
 * worker that nobody had checked. The workflow builds before running the suite;
 * this is what notices if that step is ever removed.
 */
function readBuilt(file: string): string | null {
  try {
    return readFileSync(join(DIST, file), 'utf8')
  } catch {
    if (process.env['CI']) {
      throw new Error(
        `dist/${file} is missing in CI. The e2e job must run \`pnpm build\` before ` +
          'the suite, or these service-worker checks skip silently and prove nothing.',
      )
    }

    return null
  }
}

test.describe('the built service worker', () => {
  test('precaches the app shell', () => {
    const sw = readBuilt('sw.js')
    test.skip(sw === null, 'No dist/sw.js — run `pnpm build` first.')

    const urls = [...sw!.matchAll(/url:"([^"]+)"/g)].map((match) => match[1]!)

    // The shell: the document, the bundle, the stylesheet. Without all three the
    // register does not open with the network off, which is 9.1's whole point.
    expect(urls).toContain('index.html')
    expect(urls.some((url) => url.endsWith('.js'))).toBe(true)
    expect(urls.some((url) => url.endsWith('.css'))).toBe(true)

    // And the manifest, or an installed till has no identity to launch from.
    expect(urls).toContain('manifest.webmanifest')
  })

  test('never caches an API response', () => {
    /*
     * The rule the phase doc states outright: a stale price is a wrong price,
     * and the register would have no way to know it was reading one. Data
     * caching is 9.2's job, in IndexedDB, where its age is visible on screen.
     *
     * The only mention of /api in the worker must be the navigation denylist —
     * which is the opposite of caching it.
     */
    const sw = readBuilt('sw.js')
    test.skip(sw === null, 'No dist/sw.js — run `pnpm build` first.')

    for (const strategy of ['NetworkFirst', 'CacheFirst', 'StaleWhileRevalidate']) {
      expect(sw!, `the worker installed a ${strategy} runtime cache`).not.toContain(strategy)
    }

    // Exactly one route, and it is the SPA navigation fallback.
    expect([...sw!.matchAll(/registerRoute\(/g)]).toHaveLength(1)
    expect(sw!).toContain('NavigationRoute')
  })

  test('does not let a navigation fallback swallow the API', () => {
    /*
     * Without the denylist, `/api/v1/auth/refresh` is answered with index.html
     * from the cache: a 200 carrying HTML. `refresh.ts` documents exactly what
     * that costs — `response.ok` holds, the JSON parse throws, and every
     * cashier is signed out at the first token rotation, about fifteen minutes
     * into a shift.
     */
    const sw = readBuilt('sw.js')
    test.skip(sw === null, 'No dist/sw.js — run `pnpm build` first.')

    expect(sw!).toContain(String.raw`/^\/api\//`)
    expect(sw!).toContain(String.raw`/^\/health\//`)
  })

  test('waits to be told before taking over', () => {
    /*
     * `skipWaiting()` must appear ONLY inside the message handler. Called at
     * install, a new build takes over on the next navigation — which at a
     * counter can be between the quote and the tender, so the total a cashier
     * read out and the sale that gets written come from different code.
     */
    const sw = readBuilt('sw.js')
    test.skip(sw === null, 'No dist/sw.js — run `pnpm build` first.')

    const calls = [...sw!.matchAll(/self\.skipWaiting\(\)/g)]

    expect(calls, 'skipWaiting is called somewhere other than the message handler').toHaveLength(1)
    expect(sw!).toContain('SKIP_WAITING')

    // And it claims open clients once it does take over, because a till sits on
    // one page for a whole shift and would otherwise keep running the old code.
    expect(sw!).toContain('clientsClaim')
  })

  test('is installable as a full-screen till', () => {
    const manifest = readBuilt('manifest.webmanifest')
    test.skip(manifest === null, 'No dist/manifest.webmanifest — run `pnpm build` first.')

    const parsed = JSON.parse(manifest!) as {
      display: string
      start_url: string
      icons: { sizes: string; purpose?: string }[]
    }

    // A till is a fixed device in a shop, not a page somebody browsed to.
    expect(parsed.display).toBe('fullscreen')
    expect(parsed.start_url).toBe('/register')

    // Chrome's installability floor, and a maskable one so Android does not
    // crop the glyph.
    expect(parsed.icons.map((icon) => icon.sizes)).toContain('192x192')
    expect(parsed.icons.map((icon) => icon.sizes)).toContain('512x512')
    expect(parsed.icons.some((icon) => icon.purpose === 'maskable')).toBe(true)
  })
})
