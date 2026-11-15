import { useCallback, useEffect, useState } from 'react';
import type { ReactNode } from 'react';
import { checkServer, clearServerUrl, getServerUrl, isNativeShell, notifyNative } from '../api/server';
import { useAuthStore } from '../stores/auth';
import { useNetworkStore } from '../stores/network';
import { ServerSetup } from '../pages/ServerSetup';
import styles from './ServerGate.module.css';

type Phase =
  | 'ready' /* web build, shell with a healthy server, or shell entering on cache */
  | 'setup' /* shell, first run — no address saved */
  | 'probing' /* shell, checking the saved address on boot */
  | 'down'; /* shell, saved address unreachable AND nothing cached to show — retry / change */

/**
 * MT-02: boot gate for the standalone (bundled-assets) shell. The web build
 * passes straight through (no probe, no overhead). In the shell the app boots
 * from the APK, so it renders this instead of a white screen when the server
 * is missing (first run) or unreachable (bad Wi-Fi): first-run address setup,
 * and a retry/change-server screen on a failed boot probe.
 *
 * MT-04: a signed-in user with a token gets in **even when the probe fails** —
 * the offline cache (IndexedDB) can carry the whole app, which is exactly the
 * gig case: venue Wi-Fi up, home server unreachable. The offline banner marks
 * the degraded state. Without a token there is nothing cached to show, so the
 * setup/down screen stays.
 */
export function ServerGate({ children }: { children: ReactNode }) {
  const [phase, setPhase] = useState<Phase>(() =>
    !isNativeShell() ? 'ready' : getServerUrl() ? 'probing' : 'setup',
  );

  const probe = useCallback(async () => {
    const url = getServerUrl();
    if (!url) {
      setPhase('setup');
      return;
    }
    // Keep the native update checker pointed at this server (MainActivity
    // polls {server}/api/app/version; in assets mode the bridge has no URL).
    notifyNative(url);
    const res = await checkServer(url);
    useNetworkStore.getState().setServerUnreachable(!res.ok);
    if (res.ok) {
      setPhase('ready');
      return;
    }
    // Server gone: logged-in users enter on the cache; a fresh device has
    // nothing to show, so keep the retry/change screen.
    setPhase(useAuthStore.getState().token ? 'ready' : 'down');
  }, []);

  useEffect(() => {
    if (phase === 'probing') void probe();
  }, [phase, probe]);

  if (phase === 'ready') return <>{children}</>;
  if (phase === 'setup') return <ServerSetup onConnected={() => setPhase('ready')} />;

  if (phase === 'down') {
    return (
      <div className={styles.wrap} role="status">
        <div className={styles.card}>
          <h1 className={styles.title}>Server unreachable</h1>
          <p className={styles.lede}>
            Can&apos;t reach <strong>{getServerUrl()}</strong> from this device.
          </p>
          <p className={styles.hint}>
            Check that the server is running and this device is on the same network.
          </p>
          <div className={styles.actions}>
            <button className={styles.primaryBtn} onClick={() => setPhase('probing')}>
              Try again
            </button>
            <button
              className={styles.secondaryBtn}
              onClick={() => {
                clearServerUrl();
                setPhase('setup');
              }}
            >
              Change server
            </button>
          </div>
        </div>
      </div>
    );
  }

  // probing
  return (
    <div className={styles.wrap} role="status" aria-live="polite">
      <span className={styles.spinner} aria-hidden="true" />
      <span className={styles.probingText}>Connecting to your server…</span>
    </div>
  );
}
