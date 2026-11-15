/**
 * MT-02: auth store token placement — shell: keystore (plugin), localStorage
 * blob carries only `user`; web: unchanged zustand-persist behavior.
 */
import { beforeEach, describe, expect, it, vi } from 'vitest';

vi.mock('../api/client', () => ({
  apiLogin: vi.fn(async () => ({ token: 'jwt-abc', user: { id: 'u1', username: 'agent', displayName: 'Agent' } })),
  apiRegister: vi.fn(async () => ({ token: 'jwt-reg', user: { id: 'u2', username: 'new', displayName: 'New' } })),
}));

import { useAuthStore } from './auth';

const NATIVE = {
  isNativePlatform: () => true,
  Plugins: {
    AhChord: {
      setServer: vi.fn(() => Promise.resolve()),
      setToken: vi.fn(() => Promise.resolve()),
      getToken: vi.fn(() => Promise.resolve({ token: null })),
      clearToken: vi.fn(() => Promise.resolve()),
    },
  },
};

function blob() {
  return JSON.parse(localStorage.getItem('ahchord-auth') ?? '{}')?.state;
}

beforeEach(() => {
  localStorage.clear();
  useAuthStore.setState({ token: null, user: null });
  NATIVE.Plugins.AhChord.setToken.mockClear();
  NATIVE.Plugins.AhChord.clearToken.mockClear();
});

describe('auth store — web', () => {
  it('login persists the token to the localStorage blob (unchanged behavior)', async () => {
    delete window.Capacitor;
    await useAuthStore.getState().login({ username: 'agent', password: 'x' });
    expect(useAuthStore.getState().token).toBe('jwt-abc');
    expect(blob().token).toBe('jwt-abc');
  });
});

describe('auth store — shell', () => {
  beforeEach(() => {
    window.Capacitor = NATIVE as unknown as Window['Capacitor'];
  });

  it('login writes the token to the keystore, NOT to the persisted blob', async () => {
    await useAuthStore.getState().login({ username: 'agent', password: 'x' });
    expect(NATIVE.Plugins.AhChord.setToken).toHaveBeenCalledWith({ token: 'jwt-abc' });
    expect(useAuthStore.getState().token).toBe('jwt-abc');
    // persist rewrite (set on login) strips the token from the blob; user survives
    await vi.waitFor(() => expect(localStorage.getItem('ahchord-auth')).toContain('u1'));
    expect(blob().token).toBeUndefined();
    expect(localStorage.getItem('ahchord-auth')).not.toContain('jwt-abc');
  });

  it('register rides the keystore too', async () => {
    await useAuthStore.getState().register({ username: 'new', password: 'longenough', displayName: 'New' } as never);
    expect(NATIVE.Plugins.AhChord.setToken).toHaveBeenCalledWith({ token: 'jwt-reg' });
  });

  it('logout clears the keystore token', async () => {
    await useAuthStore.getState().login({ username: 'agent', password: 'x' });
    useAuthStore.getState().logout();
    expect(NATIVE.Plugins.AhChord.clearToken).toHaveBeenCalled();
    expect(useAuthStore.getState().token).toBeNull();
  });
});

describe('auth store — ST-90 gate 4: rotateSession (password change replacement token)', () => {
  const user = { id: 'u1', email: 'agent@x.test', username: 'agent' } as never;

  it('web: the replacement lands in state and in the persisted blob', () => {
    delete window.Capacitor;
    useAuthStore.getState().rotateSession('jwt-rotated', user);
    expect(useAuthStore.getState().token).toBe('jwt-rotated');
    expect(blob().token).toBe('jwt-rotated');
  });

  it('shell: the replacement rides the keystore, never the blob', async () => {
    window.Capacitor = NATIVE as unknown as Window['Capacitor'];
    useAuthStore.getState().rotateSession('jwt-rotated', user);
    expect(NATIVE.Plugins.AhChord.setToken).toHaveBeenCalledWith({ token: 'jwt-rotated' });
    expect(useAuthStore.getState().token).toBe('jwt-rotated');
    await vi.waitFor(() => expect(localStorage.getItem('ahchord-auth')).not.toContain('jwt-rotated'));
  });
});
