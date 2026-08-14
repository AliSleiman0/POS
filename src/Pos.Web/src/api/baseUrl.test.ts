import { describe, expect, it } from 'vitest'
import { resolveApiBaseUrl } from './baseUrl'

/**
 * `API_BASE_URL` itself is resolved once at module load from `import.meta.env`,
 * which Vite bakes in at build time — so the constant cannot be re-tested under
 * different configuration. `resolveApiBaseUrl` is the seam, and these cover
 * both branches of it.
 */
describe('resolveApiBaseUrl', () => {
  const origin = 'http://localhost:5173'

  it('falls back to the current origin when nothing is configured', () => {
    // The development and Playwright path: Vite proxies /api and /health, so
    // the page's origin *is* the API. This case existing unchanged is what
    // keeps the whole e2e suite working without an env file.
    expect(resolveApiBaseUrl(undefined, origin)).toBe(origin)
  })

  it.each([
    ['an empty string', ''],
    ['whitespace only', '   '],
  ])('treats %s as not configured', (_label, configured) => {
    // `VITE_API_BASE_URL=` left in an .env file is a mistake, not a request for
    // a relative base. Honouring it would point every call at nothing.
    expect(resolveApiBaseUrl(configured, origin)).toBe(origin)
  })

  it('uses a configured absolute origin', () => {
    expect(resolveApiBaseUrl('https://pos-api.fly.dev', origin)).toBe('https://pos-api.fly.dev')
  })

  it('trims surrounding whitespace', () => {
    // A trailing newline is what a shell heredoc or a CI secret usually carries.
    expect(resolveApiBaseUrl('  https://pos-api.fly.dev\n', origin)).toBe('https://pos-api.fly.dev')
  })

  it.each([
    ['https://pos-api.fly.dev/', 'https://pos-api.fly.dev'],
    ['https://pos-api.fly.dev///', 'https://pos-api.fly.dev'],
  ])('strips the trailing slash from %s', (configured, expected) => {
    // The generated paths already start with /api/v1, so a trailing slash
    // produces https://host//api/v1/... — which some servers normalise and
    // some answer 404 for.
    expect(resolveApiBaseUrl(configured, origin)).toBe(expected)
  })

  it('keeps a path prefix, which is not the same as a trailing slash', () => {
    // An API mounted under a sub-path is a legitimate deployment. Only the
    // trailing separator is noise.
    expect(resolveApiBaseUrl('https://example.test/pos/', origin)).toBe('https://example.test/pos')
  })

  it.each([
    ['a bare host with no scheme', 'pos-api.fly.dev'],
    ['a relative path', '/api'],
    ['nonsense', 'https://'],
  ])('throws on %s', (_label, configured) => {
    // Loudly, at load. The alternative is a build that succeeds and a till that
    // fails every request with a message about the request rather than the
    // configuration that caused it.
    expect(() => resolveApiBaseUrl(configured, origin)).toThrow(/VITE_API_BASE_URL/)
  })

  it('rejects a scheme that is not http or https', () => {
    expect(() => resolveApiBaseUrl('ftp://pos-api.fly.dev', origin)).toThrow(/http or https/)
  })
})
