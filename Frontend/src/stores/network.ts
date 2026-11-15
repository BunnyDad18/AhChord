import { create } from 'zustand';
import { getMeta } from '../offline/db';
import { syncLibrary } from '../offline/sync';

/**
 * MT-04: connectivity + cache freshness, shared by the offline banner and the pages.
 * `online` follows navigator.onLine (browser connectivity events are the source);
 * `lastSyncAt` is the server stamp of the last successful /api/sync (from IndexedDB).
 * `serverUnreachable` (MT-02 shell) is set by the boot probe when the saved server
 * can't be reached — connectivity exists (venue Wi-Fi) but the home server is gone;
 * that's the gig case navigator.onLine cannot see.
 */
interface NetworkState {
  online: boolean;
  serverUnreachable: boolean;
  lastSyncAt: string | null;
  setOnline: (online: boolean) => void;
  setServerUnreachable: (v: boolean) => void;
  setLastSyncAt: (at: string | null) => void;
}

export const useNetworkStore = create<NetworkState>((set) => ({
  online: typeof navigator === 'undefined' ? true : navigator.onLine,
  serverUnreachable: false,
  lastSyncAt: null,
  setOnline: (online) => set({ online }),
  setServerUnreachable: (serverUnreachable) => set({ serverUnreachable }),
  setLastSyncAt: (lastSyncAt) => set({ lastSyncAt }),
}));

let started = false;

/**
 * Wire connectivity: hydrate lastSyncAt from the cache, react to online/offline events,
 * and delta-sync the library as soon as connectivity returns. Call once at app start
 * (Shell mounts this). Safe to call twice.
 */
export function initNetwork(): void {
  if (started || typeof window === 'undefined') return;
  started = true;

  void getMeta<string>('lastSyncAt').then((at) => {
    if (at) useNetworkStore.getState().setLastSyncAt(at);
  });

  window.addEventListener('online', () => {
    useNetworkStore.getState().setOnline(true);
    void syncLibrary().catch(() => {
      /* server still unreachable — the next event/reconnect retries */
    });
  });
  window.addEventListener('offline', () => useNetworkStore.getState().setOnline(false));
  // Venue-Wi-Fi case (shell): the boot probe flagged the server unreachable and the
  // app is running on cache — connectivity events will not fire, so retry quietly.
  window.addEventListener('focus', () => {
    const s = useNetworkStore.getState();
    if (s.serverUnreachable && s.online) void syncLibrary().catch(() => {});
  });
}
