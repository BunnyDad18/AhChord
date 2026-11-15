import { useNavigate } from 'react-router-dom';
import { useActiveSessions } from '../hooks/useActiveSessions';
import { useSessionStore } from '../stores/session';
import { useToastStore } from '../stores/toast';
import { joinHosting, leaveFollowing, startHosting, stopHosting } from '../realtime/followClient';
import { UsersIcon } from './Icons';
import ui from './ui.module.css';
import styles from './Shell.module.css';

/**
 * ST-87: the sidebar SESSION panel — the general follow service.
 *
 *   idle      — every live session on the server: "{host} · {what they're viewing}"
 *               with a Join button, plus Start session (always visible — hosting
 *               needs nothing: everything you browse from then on is broadcast).
 *   host      — "Your session is live · N following · now: {song}" + Stop.
 *   follower  — "Following {host}: {song}" + Leave.
 *
 * Collapsed rail: a users icon carrying the count (live sessions + own role).
 * (ST-86's setlist-picked "start" evolved: James — "it should be a general
 * following service… if I select a song, I should see it on the other devices".)
 *
 * Sessions hosted by YOUR OWN account on another device are listed too —
 * "follow myself onto the tablet" is the primary case (the first pass filtered
 * own-user-id sessions, which hid the session from a second device signed in
 * as the host). The idle branch only renders while THIS device is not
 * hosting/following, so a device never lists the session it hosts itself.
 */
export function SessionPanel({ collapsed }: { collapsed: boolean }) {
  const navigate = useNavigate();
  const pushToast = useToastStore((s) => s.push);
  const sessions = useActiveSessions();
  const role = useSessionStore((s) => s.role);
  const hostName = useSessionStore((s) => s.hostName);
  const followers = useSessionStore((s) => s.followers);
  const viewing = useSessionStore((s) => s.viewing);

  const badge = sessions.length + (role === 'idle' ? 0 : 1);

  async function start() {
    try {
      await startHosting();
      pushToast('Session started — everyone who joins follows what you browse.');
    } catch (e) {
      pushToast(e instanceof Error ? e.message : 'Could not start the session.', 'error');
    }
  }

  async function join(hostId: string, name: string) {
    try {
      await joinHosting(hostId);
      pushToast(`You are following ${name} — their page is now your page.`);
    } catch (e) {
      pushToast(e instanceof Error ? e.message : 'That session has ended.', 'error');
    }
  }

  if (collapsed) {
    return (
      <button
        type="button"
        className={`${styles.iconBtn} ${styles.sideIcon} ${styles.sessCollapsedBtn}`}
        onClick={() => navigate('/playlists')}
        aria-label={badge > 0 ? `${sessions.length} live session${sessions.length === 1 ? '' : 's'}` : 'Sessions'}
        title={role === 'host' ? 'Your session is live' : role === 'follower' ? `Following ${hostName}` : 'Sessions'}
      >
        <UsersIcon size={18} />
        {badge > 0 && <span className={styles.sessBadge}>{badge}</span>}
      </button>
    );
  }

  if (role === 'host') {
    return (
      <div className={styles.sessPanel} aria-label="Your session">
        <div className={styles.sessRow}>
          <span className={styles.sessDot} aria-hidden="true" />
          <span className={styles.sessName}>Your session · {followers} following</span>
        </div>
        <div className={styles.sessHint}>
          {viewing?.songTitle
            ? `Now: ${viewing.songTitle}`
            : 'Open a song — your followers follow along.'}
        </div>
        <button type="button" className={`${ui.btn} ${ui.small}`} onClick={() => void stopHosting()}>
          Stop session
        </button>
      </div>
    );
  }

  if (role === 'follower') {
    return (
      <div className={styles.sessPanel} aria-label="Following session">
        <div className={styles.sessRow}>
          <span className={styles.sessDot} aria-hidden="true" />
          <span className={styles.sessName} title={viewing?.songTitle ?? ''}>
            Following {hostName}
          </span>
        </div>
        <div className={styles.sessHint}>
          {viewing?.songTitle ? viewing.songTitle : 'Waiting for their next page…'}
        </div>
        <button type="button" className={`${ui.btn} ${ui.small}`} onClick={() => void leaveFollowing()}>
          Leave session
        </button>
      </div>
    );
  }

  return (
    <div className={styles.sessPanel}>
      {sessions.map((s) => (
        <div key={s.hostId} className={styles.sessRow}>
          <span className={styles.sessDot} aria-hidden="true" />
          <span
            className={styles.sessName}
            title={s.songTitle ? `${s.hostName} — ${s.songTitle} · ${s.followers} following` : `${s.hostName} — ${s.followers} following`}
          >
            {s.hostName}
            {s.songTitle ? ` · ${s.songTitle}` : ''}
          </span>
          <button
            type="button"
            className={`${ui.btn} ${ui.small}`}
            aria-label={`Join ${s.hostName}'s session`}
            onClick={() => void join(s.hostId, s.hostName)}
          >
            Join
          </button>
        </div>
      ))}
      {/* The general Start — hosting needs no playlist; you browse, followers follow. */}
      <button
        type="button"
        className={`${ui.btn} ${ui.small} ${ui.primary} ${styles.sessStartBtn}`}
        title="Start a session: everyone who joins follows what you browse — songs, setlists, everywhere."
        onClick={() => void start()}
      >
        <UsersIcon size={14} /> Start session
      </button>
    </div>
  );
}
