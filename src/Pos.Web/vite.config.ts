/// <reference types="vitest/config" />
import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'
import path from 'node:path'

export default defineConfig({
  plugins: [react(), tailwindcss()],
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
