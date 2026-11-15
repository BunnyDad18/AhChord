import { useNetworkStore } from '../stores/network';
import { timeAgo } from '../utils/timeAgo';
import styles from './OfflineBanner.module.css';

function useOfflineState() {
  const online = useNetworkStore((s) => s.online);
  const serverUnreachable = useNetworkStore((s) => s.serverUnreachable);
  const lastSyncAt = useNetworkStore((s) => s.lastSyncAt);
  // Offline = no device connectivity, or (shell) connectivity with an unreachable server.
  const offline = !online || serverUnreachable;
  const why = !online ? 'Offline' : 'Server unreachable';
  const stamp = lastSyncAt ? ` (synced ${timeAgo(lastSyncAt)})` : ' — not synced yet from this device';
  return { offline, why, stamp };
}

/**
 * MT-04: the offline indicator (acceptance: "offline / stale since …" is visible).
 * Full-width bar under the top bar (Shell) — content on screen comes from the local
 * cache, so the user knows why it may be stale. Covers both "no connectivity" and
 * the gig case: venue Wi-Fi up, home server unreachable (shell boot probe).
 */
export function OfflineBanner() {
  const { offline, why, stamp } = useOfflineState();
  if (!offline) return null;
  return (
    <div className={styles.bar} role="status">
      <span>
        {why} — charts, setlists and search come from the local cache{stamp}.
      </span>
    </div>
  );
}

/** Compact variant for the fullscreen player's top bar. */
export function OfflineIndicator() {
  const { offline, why, stamp } = useOfflineState();
  if (!offline) return null;
  return (
    <span className={styles.pill} role="status" title={`${why}${stamp}`}>
      {why}
    </span>
  );
}
