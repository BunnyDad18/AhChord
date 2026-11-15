/**
 * MT-02: secure token storage for the standalone shell.
 *
 * Inside the Capacitor shell the JWT lives in Android Keystore-backed encrypted
 * SharedPreferences (AhChordPlugin.setToken/getToken/clearToken) — never in
 * localStorage. Web builds change nothing: the zustand-persist store keeps the
 * token in localStorage exactly as before (save/load/clear are no-ops there).
 *
 * Store reads stay synchronous (`getState().token` everywhere) because the
 * keystore value is hydrated into the store at boot, before the first render
 * (main.tsx) — the shell's boot is async anyway.
 *
 * Same hand-rolled `window.Capacitor` bridge as api/server.ts: no npm dependency,
 * and web builds never see it.
 */

import { isNativeShell } from './server';

/** The keystore token, null on web, on failure, or when not signed in. */
export async function loadSecureToken(): Promise<string | null> {
  if (!isNativeShell()) return null;
  try {
    const r = await window.Capacitor?.Plugins?.AhChord?.getToken?.();
    return typeof r?.token === 'string' && r.token ? r.token : null;
  } catch {
    return null;
  }
}

/** Persist the JWT to the keystore (no-op on web; failures surface at next boot as re-login). */
export async function saveSecureToken(token: string): Promise<void> {
  if (!isNativeShell()) return;
  try {
    await window.Capacitor?.Plugins?.AhChord?.setToken?.({ token });
  } catch {
    /* keystore write failed — the in-memory token still works this session */
  }
}

/** Forget the keystore JWT (logout / 401). */
export async function clearSecureToken(): Promise<void> {
  if (!isNativeShell()) return;
  try {
    await window.Capacitor?.Plugins?.AhChord?.clearToken?.();
  } catch {
    /* ignore */
  }
}

/**
 * Boot hydration for the shell: move the JWT from the keystore into the store,
 * migrating it out of the legacy localStorage blob (pre-keystore builds kept it
 * in zustand-persist's `ahchord-auth` entry). Setting the store rewrites that
 * blob through `partialize`, which drops the token on native — the migration's
 * cleanup is the store's own persist write.
 *
 * Never throws and never blocks: a token problem falls back to the login screen.
 */
export async function hydrateNativeToken(setToken: (token: string | null) => void): Promise<void> {
  const keystore = await loadSecureToken();
  if (keystore) {
    setToken(keystore);
    return;
  }
  let legacy: string | null = null;
  try {
    const raw = JSON.parse(localStorage.getItem('ahchord-auth') ?? 'null');
    legacy = typeof raw?.state?.token === 'string' && raw.state.token ? raw.state.token : null;
  } catch {
    /* malformed blob — treat as absent */
  }
  if (legacy) {
    await saveSecureToken(legacy);
    setToken(legacy);
  } else {
    setToken(null); // a localStorage token with an empty keystore is untrusted — force re-login
  }
}
