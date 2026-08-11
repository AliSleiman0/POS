/*
 * `defineConfig` from vitest/config, not from vite.
 *
 * Vite's own overloads do not know about the `test` block. A triple-slash
 * reference to `vitest/config` used to cover that, because the plugin array
 * still matched Vite's plain `UserConfig`; adding VitePWA changed which overload
 * wins and the error arrived as "'test' does not exist" — pointing at a line
 * fifty below the actual cause. Importing from vitest/config settles it, and
 * makes the reference directive redundant (oxlint says so too).
 */
import { defineConfig } from 'vitest/config'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import { VitePWA } from 'vite-plugin-pwa'
import path from 'node:path'

export default defineConfig({
  plugins: [
    react(),
    tailwindcss(),

    /*
     * The service worker, and every option below is a decision rather than a default.
     *
     * `registerType: 'prompt'` is the load-bearing one. With `autoUpdate` a new
     * build installs and takes over on the next navigation — which at a till
     * means the code can change *during a sale*, between the quote and the
     * tender, with a customer at the counter. The prompt is rendered by
     * `UpdatePrompt`, which additionally refuses to apply while a cart is open.
     */
    VitePWA({
      registerType: 'prompt',
      injectRegister: null,

      workbox: {
        /*
         * The shell only. **No runtime caching of the API**, deliberately and
         * per the phase doc: a stale price is a wrong price, and the register
         * would have no way to know it was reading one. Data caching is 9.2's
         * job, per-resource, in IndexedDB where its age is visible.
         *
         * globPatterns is explicit rather than the default for the same reason
         * — it says what is in the shell instead of "whatever the build emitted".
         */
        globPatterns: ['**/*.{js,css,html,svg,woff2}'],

        /*
         * A SPA: every in-app route has to resolve to index.html offline, or
         * reloading on /register while the network is down shows the browser's
         * offline page instead of the till.
         *
         * The denylist is what keeps that from swallowing the API. Without it
         * `/api/v1/...` would be answered with index.html from the cache — a
         * 200 carrying HTML, which `refreshSession` explicitly warns about
         * because it signs every cashier out at the first token rotation.
         */
        navigateFallback: 'index.html',
        navigateFallbackDenylist: [/^\/api\//, /^\/health\//],

        // The register is left open for a whole shift. Without this, a till that
        // never navigates keeps running the code it started with even after the
        // cashier has accepted an update.
        clientsClaim: true,

        // Never automatically: `UpdatePrompt` posts SKIP_WAITING itself, once
        // the cart is empty. Taking over on install is exactly the mid-sale
        // swap `registerType: 'prompt'` exists to prevent.
        skipWaiting: false,
      },

      manifest: {
        name: 'POS Register',
        short_name: 'Register',
        description: 'Point of sale register',
        // A till is a fixed device in a shop, not a page somebody browsed to.
        display: 'fullscreen',
        orientation: 'landscape',
        start_url: '/register',
        scope: '/',
        background_color: '#ffffff',
        theme_color: '#ffffff',
        icons: [
          { src: '/icon-192.png', sizes: '192x192', type: 'image/png' },
          { src: '/icon-512.png', sizes: '512x512', type: 'image/png' },
          {
            src: '/icon-512-maskable.png',
            sizes: '512x512',
            type: 'image/png',
            purpose: 'maskable',
          },
        ],
      },

      // Off in development: a service worker caching a dev build is how a
      // change stops appearing and half an hour goes into finding out why.
      devOptions: { enabled: false },
    }),
  ],
  resolve: {
    alias: {
      '@': path.resolve(import.meta.dirname, './src'),
    },
    // pnpm's strict node_modules can hand @base-ui/react its own copy of React,
    // which makes hooks fail with "Cannot read properties of null (reading
    // 'useRef')" — React's dispatcher is per-copy. Force a single instance.
    dedupe: ['react', 'react-dom'],
  },
  server: {
    port: 5173,
    fs: {
      /*
       * The repository's `tests/fixtures` sits outside this project, and Vite
       * refuses to read outside the project root by default — correctly.
       *
       * `pricing.conformance.test.ts` imports `pricing-conformance.json` from
       * there, because the whole point of that file is that **one** corpus is
       * asserted by both the .NET suite and this one; a copy inside `src/`
       * would be a second corpus and would drift.
       *
       * Widened to that one directory rather than to the repository root. The
       * root holds `appsettings.json`, `docker-compose.yml` and the migrations,
       * and a dev server that would hand those to anything reaching
       * localhost:5173 is a worse trade than typing a longer path here. The
       * directory named below contains generated test fixtures and nothing else.
       */
      allow: [
        path.resolve(import.meta.dirname),
        path.resolve(import.meta.dirname, '../../tests/fixtures'),
      ],
    },
    proxy: {
      // Proxy the API in development so the browser sees a single origin.
      // Avoids CORS locally and keeps cookie-based refresh tokens same-site,
      // which is how production behaves behind one domain.
      // Port 5013 is what src/Pos.Api/Properties/launchSettings.json binds, and
      // `dotnet run` reads that file rather than ASPNETCORE_URLS. Use the `http`
      // profile: the `https` one also binds 7091, which switches
      // UseHttpsRedirection on and 307s every proxied request out of the proxy.
      '/api': {
        target: 'http://localhost:5013',
        changeOrigin: true,
      },
      '/health': {
        target: 'http://localhost:5013',
        changeOrigin: true,
      },
    },
  },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test/setup.ts'],
    // Playwright owns e2e/; Vitest must not try to run those specs.
    exclude: ['**/node_modules/**', '**/dist/**', '**/e2e/**'],
    server: {
      deps: {
        // @base-ui/react (shadcn's primitive layer) resolves React's CJS build
        // while the app uses ESM, giving two React instances and a null hook
        // dispatcher — "Cannot read properties of null (reading 'useRef')".
        // Inlining makes Vitest transform it through the same module graph.
        inline: [/@base-ui/],
      },
    },
    coverage: {
      provider: 'v8',
      reporter: ['text', 'html'],
    },
  },
})
