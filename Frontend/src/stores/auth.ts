import { create } from 'zustand';
import { persist } from 'zustand/middleware';
import { apiLogin, apiRegister } from '../api/client';
import type { AuthUser, LoginRequest, RegisterRequest } from '../api/types';
import { isNativeShell } from '../api/server';
import { clearSecureToken, saveSecureToken } from '../api/tokenStore';
import { useImportQueueStore } from './importQueue';

interface AuthState {
  token: string | null;
  user: AuthUser | null;
  login: (creds: LoginRequest) => Promise<void>;
  register: (req: RegisterRequest) => Promise<void>;
  logout: () => void;
  /** ST-90 gate 4: adopt the replacement token the change-password endpoint returns (with the
   *  stamp check on, the password change revokes the token that made the request, so this
   *  device would be signed out by its own success). Same placement rules as login: keystore on
   *  native, blob on web. */
  rotateSession: (token: string, user: AuthUser) => void;
  hydrate: () => void;
  /** Replace the cached user (e.g. after a display-name update, ST-26). */
  setUser: (user: AuthUser) => void;
}

/**
 * Auth store. Web: persists `token` + `user` to localStorage (zustand persist),
 * the API client reads the token from this store and clears it on a 401.
 * MT-02 shell: the JWT lives in the Android Keystore (api/tokenStore) — the
 * persisted blob carries only `user` there, and boot hydration (main.tsx)
 * reloads the token before the first render.
 */
export const useAuthStore = create<AuthState>()(
  persist(
    (set) => ({
      token: null,
      user: null,
      login: async (creds) => {
        const res = await apiLogin(creds);
        set({ token: res.token, user: res.user });
        void saveSecureToken(res.token);
      },
      register: async (req) => {
        const res = await apiRegister(req);
        set({ token: res.token, user: res.user });
        void saveSecureToken(res.token);
      },
      // ST-64: clearing the queue on sign-out keeps one user's conversion data (titles,
      // streamed previews) from flashing for the next user in the same tab.
      logout: () => {
        useImportQueueStore.getState().clear();
        set({ token: null, user: null });
        void clearSecureToken();
      },
      rotateSession: (token, user) => {
        set({ token, user });
        void saveSecureToken(token);
      },
      setUser: (user) => set({ user }),
      // Persisted state is restored synchronously from localStorage on store
      // creation; this is a safe explicit hook for boot-time sync / tests.
      hydrate: () => {
        /* no-op: state is already hydrated by the persist middleware */
      },
    }),
    {
      name: 'ahchord-auth',
      // MT-02: on native the token belongs to the keystore, never to localStorage.
      // (tokenStore.hydrateNativeToken's setState is the legacy-blob cleanup: this
      // partialize rewrites the persisted entry without the token.)
      partialize: (state) =>
        isNativeShell() ? { user: state.user } : { token: state.token, user: state.user },
    },
  ),
);
