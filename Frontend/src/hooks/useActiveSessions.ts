import { useEffect, useState } from 'react';
import { apiActiveSessions } from '../api/client';
import type { ActiveSession } from '../api/types';

/**
 * ST-86: the live band sessions on this server, polled for the Start/Join UI
 * (the "does any session exist on the network?" check — the server IS the
 * network entry point, one origin, one port).
 *
 * Polls every 5s (sessions start and end in real time; faster than the top-bar
 * activity box so a Join row appears almost as the host starts). A failed poll
 * keeps the last snapshot, so a blip never empties the UI.
 */
export function useActiveSessions(): ActiveSession[] {
  const [sessions, setSessions] = useState<ActiveSession[]>([]);

  useEffect(() => {
    let cancelled = false;
    const tick = () =>
      apiActiveSessions()
        .then((s) => {
          if (!cancelled) setSessions(s);
        })
        .catch(() => {
          /* offline / signed out — keep the last snapshot */
        });
    tick();
    const t = window.setInterval(tick, 5_000);
    return () => {
      cancelled = true;
      window.clearInterval(t);
    };
  }, []);

  return sessions;
}
