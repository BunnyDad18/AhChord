import ReactDOM from 'react-dom/client';
import { StrictMode } from 'react';
import App from './App';
import { isNativeShell } from './api/server';
import { hydrateNativeToken } from './api/tokenStore';
import { useAuthStore } from './stores/auth';
import { useUiStore } from './stores/ui';
import './theme/tokens.css';

// Apply the persisted theme + font-size before first paint (dark is the default).
document.documentElement.setAttribute('data-theme', useUiStore.getState().theme);
document.documentElement.setAttribute('data-font-size', useUiStore.getState().fontSize);

// Bug report (2026-11-12, /songs): the browser's native scroll restoration fights
// the SPA's own list-position restore (Songs.tsx ST-51 #15) — on back-navigation
// Chrome restores toward the old offset while the page is still a skeleton (short
// document → clamps to top and locks the position in), so returning from a song
// landed at the top. Scroll is app-owned; the browser keeps its hands off.
if ('scrollRestoration' in history) history.scrollRestoration = 'manual';

const rootEl = document.getElementById('root');
if (!rootEl) {
  throw new Error('Root element #root not found');
}

const root = ReactDOM.createRoot(rootEl);
const render = () =>
  root.render(
    <StrictMode>
      <App />
    </StrictMode>,
  );

// MT-02: in the shell the JWT lives in the Android Keystore, not localStorage —
// hydrate it into the auth store before the first render (boot is async there
// anyway); a token problem never blocks boot, the login screen is the fallback.
if (isNativeShell()) {
  hydrateNativeToken((token) => useAuthStore.setState({ token })).then(render, render);
} else {
  render();
}

// MT-04: register the service worker (app shell offline + PWA install). Prod builds only —
// autoUpdate swaps in the new shell on the next visit, no user prompt.
// Bug report (2026-11-13, "still returns to the top" — the scroll-restore fix never reached
// the phone): autoUpdate installs the new SW, but a long-open PWA window checks for updates
// only on load, so deploys lag by whole sessions. Two closers: check on an interval while
// open, and reload when the new SW takes control. A fix that is not in the running client
// is not shipped (AHC-8, web side).
if (import.meta.env.PROD) {
  import('virtual:pwa-register').then(({ registerSW }) => {
    registerSW({
      immediate: true,
      onRegisteredSW(_swUrl, reg) {
        // The build client hands us the registration: re-check sw.js on an interval so a
        // long-open practice window (never closed between deploys) notices the new shell.
        if (reg) window.setInterval(() => void reg.update().catch(() => {}), 60 * 60 * 1000);
      },
    });
    // Only a page that STARTED under a worker listens: a first-load install would take
    // control once (null -> worker) and reload itself forever.
    if (navigator.serviceWorker?.controller) {
      navigator.serviceWorker.addEventListener('controllerchange', () => window.location.reload());
    }
  });
}
