import { useEffect, useState } from 'react';
import { ContractIcon, ExpandIcon } from './Icons';
import styles from './FullscreenToggle.module.css';

/**
 * Bug report (2026-11-12, "/"): a full-screen button so the browser bar disappears.
 * The Fullscreen API's requestFullscreen is a user-gesture-only API — it lives on a
 * button, not an effect: the Shell mounts it on every page (sidebar foot / top bar),
 * the player dock carries its own. iOS Safari has no element Fullscreen API — the
 * control renders nothing where it cannot work (the PWA's display-mode: standalone
 * stays the iOS path). jsdom has no API either, so tests render the null branch.
 */
export function FullscreenToggle({ className }: { className?: string }) {
  const supported =
    typeof document !== 'undefined' &&
    typeof document.documentElement.requestFullscreen === 'function';
  const [active, setActive] = useState(() => document.fullscreenElement !== null);

  useEffect(() => {
    if (!supported) return;
    const onChange = () => setActive(document.fullscreenElement !== null);
    document.addEventListener('fullscreenchange', onChange);
    return () => document.removeEventListener('fullscreenchange', onChange);
  }, [supported]);

  if (!supported) return null;

  return (
    <button
      type="button"
      className={`${styles.btn} ${className ?? ''}`.trim()}
      onClick={() => {
        if (document.fullscreenElement) void document.exitFullscreen().catch(() => undefined);
        else void document.documentElement.requestFullscreen().catch(() => undefined);
      }}
      aria-label={active ? 'Exit full screen' : 'Full screen'}
      title={active ? 'Exit full screen' : 'Full screen (hide the browser bar)'}
    >
      {active ? <ContractIcon size={18} /> : <ExpandIcon size={18} />}
    </button>
  );
}
