/**
 * Where the API lives.
 *
 * Until Phase 8 there was one answer — `window.location.origin` — and it was
 * right only because Vite proxies `/api` in development, so the browser saw a
 * single origin. A deployed web app is served from its own static host and the
 * API runs in a container somewhere else, so the SPA would otherwise ask
 * *itself* for `/api/v1/...` and get its own `index.html` back with a 200. That
 * failure is worse than a 404: `openapi-fetch` hands the HTML to `unwrap`,
 * which sees `response.ok` and returns garbage typed as a sale.
 *
 * The variable is read once, at module load, and validated there.
 */

/**
 * Resolves the configured base URL, or falls back to the current origin.
 *
 * Separate from the module-level constant below so both branches are reachable
 * from a test: `import.meta.env` is baked in at build time and cannot be
 * changed after this module has been imported.
 *
 * @param configured `VITE_API_BASE_URL`, or undefined when it is not set.
 * @param fallback The origin to use when nothing is configured.
 * @throws {Error} if a value is set but is not a usable absolute origin.
 */
export function resolveApiBaseUrl(configured: string | undefined, fallback: string): string {
  const trimmed = configured?.trim()

  // Empty and whitespace count as "not set". An `.env` line left as
  // `VITE_API_BASE_URL=` is a mistake, and treating it as the empty string
  // would make every request relative to nothing.
  if (trimmed === undefined || trimmed === '') {
    return stripTrailingSlashes(fallback)
  }

  let parsed: URL

  try {
    parsed = new URL(trimmed)
  } catch {
    // Loudly, at load. The alternative is every API call failing later with a
    // message about the request rather than about the configuration.
    throw new Error(
      `VITE_API_BASE_URL is not a valid absolute URL: ${JSON.stringify(trimmed)}. ` +
        'Expected something like https://pos-api.example.com',
    )
  }

  if (parsed.protocol !== 'https:' && parsed.protocol !== 'http:') {
    throw new Error(
      `VITE_API_BASE_URL must be http or https, not ${JSON.stringify(parsed.protocol)}.`,
    )
  }

  return stripTrailingSlashes(trimmed)
}

/**
 * The generated paths already begin with `/api/v1`, so a base ending in `/`
 * produces `https://host//api/v1/...`. Some servers normalise that and some
 * answer 404; none of them should have to.
 */
function stripTrailingSlashes(value: string): string {
  return value.replace(/\/+$/, '')
}

/**
 * The resolved base. `window.location.origin` when unset, which is what keeps
 * `pnpm dev` and the whole Playwright suite on the existing proxy path.
 */
export const API_BASE_URL = resolveApiBaseUrl(
  import.meta.env.VITE_API_BASE_URL,
  window.location.origin,
)
