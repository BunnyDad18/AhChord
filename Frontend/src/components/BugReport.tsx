import { useState } from 'react';
import { apiBugReport } from '../api/client';
import { useToastStore } from '../stores/toast';
import { BugIcon } from './Icons';
import ui from './ui.module.css';
import styles from './BugReport.module.css';

interface BugReportProps {
  /** ST-39 D1: extra context appended to the report (e.g. the song in the player). */
  context?: string;
  /**
   * Report (2026-11-12, "can't see button to enter bug report on song page"):
   * the phone More sheet renders its OWN labeled trigger (the bare bug icon was
   * easy to miss); hideTrigger mounts just the dialog.
   */
  hideTrigger?: boolean;
}

/**
 * ST-35 H2 / ST-36 C1: in-app bug report — a flat bug button that opens a dialog for bug
 * notes; submissions append to /data/bug_reports.md on the server (timestamp + user).
 * Self-contained so it can live in both the header (Shell) and the player's top bar.
 * ST-39 D1: every report carries where it came from — page path (+ optional context).
 */
export function BugReport({ context, hideTrigger = false }: BugReportProps) {
  const pushToast = useToastStore((s) => s.push);
  const [open, setOpen] = useState(false);
  const [text, setText] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function send() {
    const message = text.trim();
    if (!message || busy) return;
    setBusy(true);
    setError(null);
    try {
      // ST-39 D1: append the page (and context, e.g. which song) so reports carry their own trail.
      const page = window.location.pathname;
      await apiBugReport(`${message}\n\n---\nPage: ${page}${context ? ` · ${context}` : ''}`);
      pushToast('Bug report sent — thanks!');
      setText('');
      setOpen(false);
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : 'Could not send the report.');
    } finally {
      setBusy(false);
    }
  }

  return (
    <>
      {!hideTrigger && (
        <button
          type="button"
          className={styles.btn}
          onClick={() => setOpen(true)}
          aria-label="Report a bug"
          title="Report a bug"
        >
          <BugIcon size={20} />
        </button>
      )}

      {open && (
        <div
          className={styles.overlay}
          role="dialog"
          aria-modal="true"
          aria-label="Report a bug"
          onClick={() => {
            if (!busy) setOpen(false);
          }}
        >
          <div className={styles.panel} onClick={(e) => e.stopPropagation()}>
            <h2 className={styles.title}>Report a bug</h2>
            <p className={styles.hint}>
              What happened? The note is saved on this server for review — no account details
              needed.
            </p>
            <textarea
              className={styles.text}
              value={text}
              onChange={(e) => setText(e.target.value)}
              placeholder="Describe the problem (what you did, what happened, what you expected)…"
              autoFocus
            />
            {error && (
              <p className={styles.error} role="alert">
                {error}
              </p>
            )}
            <div className={styles.actions}>
              <button
                type="button"
                className={ui.btn}
                disabled={busy}
                onClick={() => setOpen(false)}
              >
                Cancel
              </button>
              <button
                type="button"
                className={`${ui.btn} ${ui.primary}`}
                disabled={busy || !text.trim()}
                onClick={() => void send()}
              >
                {busy ? 'Sending…' : 'Send report'}
              </button>
            </div>
          </div>
        </div>
      )}
    </>
  );
}
