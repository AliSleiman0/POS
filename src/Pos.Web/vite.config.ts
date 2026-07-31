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
    proxy: {
      // Proxy the API in development so the browser sees a single origin.
      // Avoids CORS locally and keeps cookie-based refresh tokens same-site,
      // which is how production behaves behind one domain.
      '/api': {
        target: 'http://localhost:5199',
        changeOrigin: true,
      },
      '/health': {
        target: 'http://localhost:5199',
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
