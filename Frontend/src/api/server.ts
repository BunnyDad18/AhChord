/**
 * MT-02: runtime server configuration for the standalone (bundled-assets) shell.
 *
 * The web build stays exactly as before: relative `/api` + `/hubs` paths.
 * The Capacitor shell loads this same bundle from its own local origin
 * (http://localhost), where relative paths hit the local server, not the
 * user's AhChord instance — so there the base URL is built from the server
 * address the user entered on first run (persisted in localStorage under
 * `ahchord.server`).
 *
 * Native-shell detection uses the `window.Capacitor` global that the shell's
 * local server injects — no npm dependency, and web builds never see it.
 */

declare global {
  interface Window {
    Capacitor?: {
      isNativePlatform?: () => boolean;
      Plugins?: {
        AhChord?: {
          setServer?: (args: { url: string }) => Promise<unknown>;
          setToken?: (args: { token: string }) => Promise<unknown>;
          getToken?: () => Promise<{ token?: string | null }>;
          clearToken?: () => Promise<unknown>;
        };
      };
    };
  }
}

const STORAGE_KEY = 'ahchord.server';

/** True inside the Capacitor shell (Android phone / Shield Pro); false in a browser. */
export function isNativeShell(): boolean {
  return (
    typeof window !== 'undefined' &&
    typeof window.Capacitor?.isNativePlatform === 'function' &&
    window.Capacitor.isNativePlatform()
  );
}

/**
 * True when cleartext HTTP is the sensible DEFAULT for this host: loopback,
 * RFC 1918 / link-local IPv4, single-label LAN names, and the `.local` mDNS zone.
 * MT-02 scoped cleartext: Android's declarative config cannot scope to address
 * ranges, so the scope is enforced in-app at the point where a bare address
 * gains its scheme — a bare public name (and the TLS-served `.test` zone)
 * defaults to https; an address whose scheme the user typed explicitly is
 * respected as entered (the "user-entered addresses" clause of the MT-02 rule).
 */
export function isPrivateHost(host: string): boolean {
  const h = host.toLowerCase();
  if (h === 'localhost' || h === '::1' || h.endsWith('.localhost')) return true;
  if (h.endsWith('.local')) return true;
  if (!h.includes('.')) return true; // single-label names are LAN/NetBIOS hosts, never public
  const m = h.match(/^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$/);
  if (!m) return false;
  const [a, b] = [Number(m[1]), Number(m[2])];
  return a === 10 || (a === 172 && b >= 16 && b <= 31) || (a === 192 && b === 168) || a === 127 || (a === 169 && b === 254);
}

/**
 * Normalize a user-entered server address to a bare origin (`http://10.0.0.9:5000`
 * for a LAN instance, `https://box.example.com` for a public one), adding a scheme
 * when omitted. Cleartext is scoped to private/LAN hosts (isPrivateHost): a bare
 * public name and any `http://` public address become `https://`. null when the
 * input is not a usable http(s) origin. The path is dropped — an instance is
 * served at root.
 */
export function normalizeServerUrl(input: string): string | null {
  let s = input.trim();
  if (!s) return null;
  const bare = !/^[a-zA-Z][a-zA-Z0-9+.-]*:\/\//.test(s);
  if (bare) s = `http://${s}`;
  let u: URL;
  try {
    u = new URL(s);
  } catch {
    return null;
  }
  if (u.protocol !== 'http:' && u.protocol !== 'https:') return null;
  if (!u.hostname) return null;
  // MT-02 scoped cleartext: the scheme a BARE address gets is chosen by host scope
  // (private/LAN → http, public + the TLS-served .test zone → https); an explicitly
  // typed scheme is respected as entered.
  if (bare && u.protocol === 'http:' && !isPrivateHost(u.hostname)) u.protocol = 'https:';
  return u.origin;
}

/** The saved server origin, null when first-run setup hasn't completed. */
export function getServerUrl(): string | null {
  try {
    return window.localStorage.getItem(STORAGE_KEY);
  } catch {
    return null;
  }
}

/** Persist the server origin and hand it to the native shell (update checker). */
export function setServerUrl(url: string): void {
  try {
    window.localStorage.setItem(STORAGE_KEY, url);
  } catch {
    /* storage unavailable — the value stays in memory for this session */
  }
  notifyNative(url);
}

/** Forget the server address (Settings → change server → first-run screen). */
export function clearServerUrl(): void {
  try {
    window.localStorage.removeItem(STORAGE_KEY);
  } catch {
    /* ignore */
  }
}

/**
 * Keep the native side current: MainActivity's in-app update flow polls
 * {server}/api/app/version, and in bundled-assets mode the bridge no longer
 * knows the address (MT-01 read it from server.url). Missing plugin (web
 * build, older APK) → no-op.
 */
export function notifyNative(url: string): void {
  try {
    window.Capacitor?.Plugins?.AhChord?.setServer?.({ url })?.catch?.(() => {
      /* older APK without the plugin — web keeps working */
    });
  } catch {
    /* ignore */
  }
}

const ENV_API_BASE = import.meta.env.VITE_API_BASE ?? '/api';
const ENV_HUB_URL = import.meta.env.VITE_HUB_URL ?? '/hubs/session';

/** API base for every fetch: the saved server in the shell, same-origin on the web. */
export function apiBase(): string {
  const server = isNativeShell() ? getServerUrl() : null;
  return server ? `${server}/api` : ENV_API_BASE;
}

/** SignalR hub URL, same rule as apiBase(). */
export function hubUrl(): string {
  const server = isNativeShell() ? getServerUrl() : null;
  return server ? `${server}/hubs/session` : ENV_HUB_URL;
}

export type ServerCheck = { ok: true } | { ok: false; error: string };

/**
 * Probe a candidate address via GET {base}/api/health. Returns a
 * user-presentable error on any failure (bad input, offline, not an AhChord).
 */
export async function checkServer(raw: string, timeoutMs = 8000): Promise<ServerCheck> {
  const base = normalizeServerUrl(raw);
  if (!base) {
    return { ok: false, error: 'That does not look like an address. Try http://192.168.1.50:5000' };
  }
  // AbortController (not AbortSignal.timeout) — the Shield's WebView may predate it.
  const ctrl = new AbortController();
  const timer = setTimeout(() => ctrl.abort(), timeoutMs);
  try {
    const res = await fetch(`${base}/api/health`, { signal: ctrl.signal });
    if (!res.ok) {
      return { ok: false, error: `The server answered ${res.status} — that address is not an AhChord server.` };
    }
    return { ok: true };
  } catch {
    return {
      ok: false,
      error: 'Could not reach that address. Check the URL, the Wi-Fi, and that the server is running.',
    };
  } finally {
    clearTimeout(timer);
  }
}
