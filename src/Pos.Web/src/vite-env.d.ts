/// <reference types="vite/client" />

/**
 * The environment variables this app reads, declared rather than inferred.
 *
 * Vite's own `ImportMetaEnv` carries an index signature typed `any`, so
 * `import.meta.env.VITE_ANYTHING_AT_ALL` compiles and a typo produces
 * `undefined` at runtime with nothing to catch it. CLAUDE.md forbids `any`;
 * declaring the keys here is what makes that rule reach configuration too.
 */
interface ImportMetaEnv {
  /**
   * Absolute origin of the API, e.g. `https://pos-api.fly.dev`.
   *
   * Unset in development and in the Playwright suite, where Vite proxies
   * `/api` and `/health` to the API on :5013 so the browser sees a single
   * origin. Set at build time for a deployed web app, which is served from its
   * own static host and is therefore cross-origin to the API.
   */
  readonly VITE_API_BASE_URL?: string
}

interface ImportMeta {
  readonly env: ImportMetaEnv
}
