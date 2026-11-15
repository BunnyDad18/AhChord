import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { VitePWA } from 'vite-plugin-pwa';

export default defineConfig({
  plugins: [
    react(),
    // MT-04: the app shell goes offline-capable — a precached index.html + every built
    // chunk (pages are lazy chunks, so all of them) lets the app boot with no network at
    // all. API data is NOT touched here: pages read the IndexedDB cache (src/offline/),
    // and /api + /hubs + the server tools are excluded from the service worker entirely.
    VitePWA({
      registerType: 'autoUpdate',
      includeAssets: ['favicon.svg'],
      manifest: {
        name: 'AhChord',
        short_name: 'AhChord',
        description: 'AhChord — a self-hosted chord chart manager and player.',
        start_url: '/',
        scope: '/',
        display: 'standalone',
        background_color: '#1a1a2e',
        theme_color: '#1a1a2e',
        icons: [
          { src: '/icons/icon-192.png', sizes: '192x192', type: 'image/png' },
          { src: '/icons/icon-512.png', sizes: '512x512', type: 'image/png' },
          { src: '/icons/icon-maskable-512.png', sizes: '512x512', type: 'image/png', purpose: 'maskable' },
        ],
      },
      workbox: {
        globPatterns: ['**/*.{js,css,html,svg,png,woff2}'],
        navigateFallback: '/index.html',
        navigateFallbackDenylist: [
          /^\/api\//,
          /^\/hubs\//,
          /^\/scalar/,
          /^\/hangfire/,
          /^\/openapi/,
        ],
      },
    }),
  ],
  test: {
    // ST-62: component-mount tests (StrictMode effect cycles) run in jsdom.
    environmentMatchGlobs: [['**/*.dom.test.{ts,tsx}', 'jsdom']],
  },
  server: {
    port: 5173,
    proxy: {
      '/api': {
        target: 'http://localhost:5000',
        changeOrigin: true,
      },
      // SignalR (ST-25): needs WebSocket upgrade, so ws: true.
      '/hubs': {
        target: 'http://localhost:5000',
        changeOrigin: true,
        ws: true,
      },
    },
  },
});
