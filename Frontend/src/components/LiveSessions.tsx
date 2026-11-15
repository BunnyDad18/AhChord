import { useToastStore } from '../stores/toast';
import { useActiveSessions } from '../hooks/useActiveSessions';
import { joinHosting } from '../realtime/followClient';
import { UsersIcon } from './Icons';
import ui from './ui.module.css';
import styles from './LiveSessions.module.css';

/**
 * ST-87: the LIVE SESSIONS bar (phone/tablet — CSS hides it at ≥1024px where
 * the sidebar panel carries it). "Is any session running on the network?": the
 * sessions polled from GET /api/sessions ARE the network's sessions; each row
 * shows who hosts and what they're viewing, with a Join button (the follower
 * takes the host's view, the SessionWatcher follows from there). Sessions
 * hosted by your own account on another device are listed too — following
 * yourself onto the tablet is the primary case. Nothing running → nothing
 * rendered.
 */
export function LiveSessions() {
  const pushToast = useToastStore((s) => s.push);
  const sessions = useActiveSessions();

  if (sessions.length === 0) return null;

  async function join(hostId: string, hostName: string) {
    try {
      await joinHosting(hostId);
      pushToast(`You are following ${hostName} — their page is now your page.`);
    } catch (e) {
      pushToast(e instanceof Error ? e.message : 'That session has ended.', 'error');
    }
  }

  return (
    <div className={styles.bar} aria-label="Live sessions">
      {sessions.map((s) => (
        <div key={s.hostId} className={styles.row}>
          <span className={styles.dot} aria-hidden="true" />
          <span className={styles.name}>{s.hostName}</span>
          <span className={`${ui.muted} ${styles.meta}`}>
            <UsersIcon size={13} />
            {s.songTitle ? `${s.songTitle} · ` : ''}
            {s.followers} following
          </span>
          <button
            type="button"
            className={`${ui.btn} ${ui.small} ${ui.primary}`}
            aria-label={`Join ${s.hostName}'s session`}
            onClick={() => void join(s.hostId, s.hostName)}
          >
            Join
          </button>
        </div>
      ))}
    </div>
  );
}
