import { useEffect, useState, type ReactNode } from 'react';
import { NavLink } from 'react-router-dom';
import { apiMetadataScanProgress, apiPendingProposalCount } from '../api/client';
import { MetadataScanProgress } from '../api/types';
import { useImportQueueStore } from '../stores/importQueue';
import { useSessionActivityStore } from '../stores/sessionActivity';
import { RefreshIcon, ReviewIcon, SearchIcon, UsersIcon } from './Icons';
import styles from './Shell.module.css';

/**
 * ST-70g (variant B, mockup-approved 2026-11-01): the top-bar ACTIVITY BOX.
 *
 *   [icon] {automated work left} · {eye} {items to review}
 *   ▂▂▂ progress bar on the box's bottom edge ▂▂▂
 *
 * Left accent + steady glow in the state colour: teal while the machine works,
 * amber when the ball is in the user's court (only review remains).
 *
 * Automated sources (registry, priority = who wins the icon + bar):
 *   playing session > server sync (MT-04, slots in later) > converting a song
 *   > metadata scan. Each reports the items it has LEFT; the teal number sums
 *   them. User work (pending scan proposals to review) rides as its own amber
 *   count — "134 songs to review" is work, not progress.
 * Nothing anywhere = nothing rendered.
 */
interface ActivitySource {
  id: string;
  /** Lower wins the icon + bar. */
  priority: number;
  /** Items of work remaining (0 = not active). */
  count: number;
  /** Tooltip fragment, e.g. "Metadata scan: 263 of 266 songs left". */
  label: string;
  icon: ReactNode;
  href: string;
  /** Determinate bar (0–100) or indeterminate slide. */
  bar: { pct: number } | 'indet';
}

export function ActivityProgress() {
  const [scan, setScan] = useState<MetadataScanProgress | null>(null);
  const [proposals, setProposals] = useState(0);

  const sessionActive = useSessionActivityStore((s) => s.active);
  const items = useImportQueueStore((s) => s.items);
  const queueLeft = items.filter((i) => i.status === 'running' || i.status === 'queued').length;
  const scanRunning = scan?.running === true;

  // Poll: fast while a scan runs (the Songs page strip uses the same cadence),
  // slow otherwise — the box appears on its own when activity starts. Pending
  // proposals ride the same tick: a scan keeps adding them, accepting refreshes.
  useEffect(() => {
    let cancelled = false;
    const tick = () => {
      apiMetadataScanProgress()
        .then((p) => !cancelled && setScan(p))
        .catch(() => {});
      apiPendingProposalCount()
        .then((n) => !cancelled && setProposals(n))
        .catch(() => {});
    };
    tick();
    const t = window.setInterval(tick, scanRunning ? 4000 : 60_000);
    return () => {
      cancelled = true;
      window.clearInterval(t);
    };
  }, [scanRunning]);

  // Scan work left = songs not yet scanned (the endpoint keeps running=true on
  // the final song, so clamp: while running, at least 1 item remains).
  const scanLeft = scanRunning ? Math.max(1, (scan?.total ?? 0) - (scan?.index ?? 0)) : 0;
  const pct =
    scanRunning && (scan?.total ?? 0) > 0
      ? Math.min(100, Math.round(((scan?.index ?? 0) / (scan?.total ?? 1)) * 100))
      : 0;

  const automated: ActivitySource[] = [
    {
      id: 'session',
      priority: 1,
      count: sessionActive ? 1 : 0, // a live session is one ongoing activity
      label: 'Playing in a band session',
      icon: <UsersIcon size={14} />,
      href: '/',
      bar: 'indet',
    },
    // Server sync (MT-04) registers here as priority 2 when the offline cache lands:
    // { id: 'sync', priority: 2, count: pendingChanges, label: `Syncing: ${n} left`,
    //   icon: <SyncIcon size={14} />, href: '/settings', bar: { pct } },
    // (SyncIcon is already in Icons.tsx for that.)
    {
      id: 'conversions',
      priority: 3,
      count: queueLeft,
      // Bug report #103: hovering the progress icon names the songs being converted
      // (top 5) — the count alone used to say nothing about what was running.
      label: (() => {
        const names = items
          .filter((i) => i.status === 'running' || i.status === 'queued')
          .map((i) => i.extractedTitle ?? i.inputPreview?.slice(0, 40) ?? 'Untitled')
          .slice(0, 5);
        return `${queueLeft} song${queueLeft === 1 ? '' : 's'} to convert${
          names.length > 0 ? `\n${names.join('\n')}` : ''
        }`;
      })(),
      icon: (
        <span aria-hidden="true" className={styles.spinIcon}>
          <RefreshIcon size={14} />
        </span>
      ),
      href: '/queue',
      bar: 'indet',
    },
    {
      id: 'scan',
      priority: 4,
      count: scanLeft,
      label: `Metadata scan: ${scanLeft} of ${scan?.total ?? '?'} songs left`,
      icon: <SearchIcon size={14} />,
      href: '/songs',
      bar: { pct },
    },
  ];

  const active = automated.filter((s) => s.count > 0).sort((a, b) => a.priority - b.priority);
  const top = active[0];
  const autoLeft = active.reduce((n, s) => n + s.count, 0);
  const reviewLeft = proposals;
  if (!top && reviewLeft === 0) return null;

  // Amber = the machine is done, the ball is in the user's court.
  const attention = !top;
  const label = [
    ...active.map((s) => s.label),
    ...(reviewLeft > 0
      ? [`${reviewLeft} metadata proposal${reviewLeft === 1 ? '' : 's'} to review`]
      : []),
  ].join(' · ');

  return (
    <NavLink
      to={top ? top.href : '/songs'}
      className={attention ? `${styles.activityPill} ${styles.activityPillAttention}` : styles.activityPill}
      title={label}
      aria-label={label}
    >
      <span className={styles.activityHead}>
        {top && (
          <>
            <span className={styles.activityIcoAuto}>{top.icon}</span>
            <span className={styles.activityNumAuto}>{autoLeft}</span>
          </>
        )}
        {reviewLeft > 0 && (
          <>
            {top && <span className={styles.activitySep}>·</span>}
            <span className={styles.activityIcoUser}>
              <ReviewIcon size={13} />
            </span>
            <span className={styles.activityNumUser}>{reviewLeft}</span>
          </>
        )}
      </span>
      {/* Bar on the box's bottom edge: teal progress while automated work runs
          (real % for scans, slide otherwise); a full amber strip when only the
          review backlog remains. */}
      <span className={styles.activityBar} aria-hidden="true">
        {top ? (
          top.bar === 'indet' ? (
            <span className={styles.activityBarIndet} />
          ) : (
            <span className={styles.activityBarFill} style={{ width: `${top.bar.pct}%` }} />
          )
        ) : (
          <span className={styles.activityBarAmber} />
        )}
      </span>
    </NavLink>
  );
}
