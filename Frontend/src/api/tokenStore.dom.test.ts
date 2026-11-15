/**
 * MT-02: secure token storage bridge + boot hydration (keystore ⇄ store,
 * legacy localStorage migration).
 */
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { clearSecureToken, hydrateNativeToken, loadSecureToken, saveSecureToken } from './tokenStore';

const NATIVE = {
  isNativePlatform: () => true,
  Plugins: {
    AhChord: {
      setServer: vi.fn(() => Promise.resolve()),
      setToken: vi.fn(() => Promise.resolve()),
      getToken: vi.fn(() => Promise.resolve({ token: null as string | null })),
      clearToken: vi.fn(() => Promise.resolve()),
    },
  },
};

function asNative() {
  window.Capacitor = NATIVE as unknown as Window['Capacitor'];
}

beforeEach(() => {
  localStorage.clear();
  delete window.Capacitor;
  NATIVE.Plugins.AhChord.setToken.mockClear();
  NATIVE.Plugins.AhChord.getToken.mockResolvedValue({ token: null });
  NATIVE.Plugins.AhChord.clearToken.mockClear();
});

describe('tokenStore bridge', () => {
  it('web: every call is a no-op (localStorage zustand-persist stays the store)', async () => {
    await expect(loadSecureToken()).resolves.toBeNull();
    await saveSecureToken('jwt');
    await clearSecureToken();
  });

  it('shell: save/load/clear ride on the plugin', async () => {
    asNative();
    await saveSecureToken('jwt-1');
    expect(NATIVE.Plugins.AhChord.setToken).toHaveBeenCalledWith({ token: 'jwt-1' });
    NATIVE.Plugins.AhChord.getToken.mockResolvedValue({ token: 'jwt-1' });
    await expect(loadSecureToken()).resolves.toBe('jwt-1');
    await clearSecureToken();
    expect(NATIVE.Plugins.AhChord.clearToken).toHaveBeenCalled();
  });

  it('shell: plugin failure reads as "no token", never throws', async () => {
    asNative();
    NATIVE.Plugins.AhChord.getToken.mockRejectedValue(new Error('keystore down'));
    await expect(loadSecureToken()).resolves.toBeNull();
  });
});

describe('hydrateNativeToken', () => {
  it('keystore token wins and lands in the store', async () => {
    asNative();
    NATIVE.Plugins.AhChord.getToken.mockResolvedValue({ token: 'from-keystore' });
    const setToken = vi.fn();
    await hydrateNativeToken(setToken);
    expect(setToken).toHaveBeenCalledWith('from-keystore');
  });

  it('pre-keystore build: migrates the legacy zustand-persist blob into the keystore', async () => {
    asNative();
    localStorage.setItem('ahchord-auth', JSON.stringify({ state: { token: 'legacy-jwt', user: { id: 1 } }, version: 0 }));
    const setToken = vi.fn();
    await hydrateNativeToken(setToken);
    expect(NATIVE.Plugins.AhChord.setToken).toHaveBeenCalledWith({ token: 'legacy-jwt' });
    expect(setToken).toHaveBeenCalledWith('legacy-jwt');
  });

  it('empty keystore + empty blob → null (login screen)', async () => {
    asNative();
    const setToken = vi.fn();
    await hydrateNativeToken(setToken);
    expect(setToken).toHaveBeenCalledWith(null);
  });

  it('first 0.4.9 boot: a live localStorage session migrates, not drops', async () => {
    asNative();
    NATIVE.Plugins.AhChord.getToken.mockResolvedValue({ token: null });
    localStorage.setItem('ahchord-auth', JSON.stringify({ state: { token: 'stale-jwt' } }));
    const setToken = vi.fn();
    await hydrateNativeToken(setToken);
    // migrated (first 0.4.9 boot keeps the session), so: saved + used
    expect(NATIVE.Plugins.AhChord.setToken).toHaveBeenCalledWith({ token: 'stale-jwt' });
    expect(setToken).toHaveBeenCalledWith('stale-jwt');
  });

  it('malformed blob does not throw', async () => {
    asNative();
    localStorage.setItem('ahchord-auth', '{not json');
    const setToken = vi.fn();
    await hydrateNativeToken(setToken);
    expect(setToken).toHaveBeenCalledWith(null);
  });
});
