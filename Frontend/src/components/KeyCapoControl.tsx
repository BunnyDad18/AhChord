import { useEffect, useRef, useState } from 'react';
import { useTransposeStore } from '../stores/transpose';
import { CapoControl } from './CapoControl';
import { GuitarIcon } from './Icons';
import { TransposeControl, effectiveKey, formatTranspose } from './TransposeControl';
import ui from './ui.module.css';
import styles from './KeyCapoControl.module.css';

interface KeyCapoControlProps {
  /** The song's stored key (e.g. "G"). */
  songKey?: string | null;
  /** The song's stored capo fret 0–6. */
  capoFret: number;
  /** Persist a new capo (the page PUTs it). */
  onCommitCapo: (fret: number) => void;
  disabled?: boolean;
  /** Persist transpose per song (song page true; setlist player false — the session owns it). */
  persist?: boolean;
  /** ST-75: 'up' opens the popover ABOVE the trigger (player bottom dock). */
  placement?: 'down' | 'up';
}

/**
 * ST-75: the folded Key & Capo control — one button that reads
 * "Capo 2 · Key of G" and opens a popover holding the capo stepper,
 * the transpose stepper + play-in-key select, and the resulting key. Replaces
 * the three side-by-side clusters in the song-view / player headers. Same
 * open/close pattern as RowMenu.
 * ST-77 (bug report #116): the capo moves the SHAPES (the projection shifts by
 * transpose − capo), the key you sound = chart key + transpose. Capo alone keeps
 * the key; transpose moves both.
 */
export function KeyCapoControl({
  songKey,
  capoFret,
  onCommitCapo,
  disabled = false,
  persist = true,
  placement = 'down',
}: KeyCapoControlProps) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  const dialogRef = useRef<HTMLDivElement>(null);
  const transpose = useTransposeStore((s) => s.transpose);
  const setTransient = useTransposeStore((s) => s.setTransient);
  const setTranspose = useTransposeStore((s) => s.setTranspose);

  useEffect(() => {
    if (!open) return;
    // ST-75 review: a role="dialog" popover should receive the keyboard.
    dialogRef.current?.focus();
    function onDoc(e: MouseEvent) {
      if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false);
    }
    function onKey(e: KeyboardEvent) {
      // ST-75 review: stop propagation — Esc closing this popover must not also
      // reach the player's window-level Esc handler (which exits the player).
      if (e.key === 'Escape') {
        setOpen(false);
        e.stopPropagation();
      }
    }
    document.addEventListener('mousedown', onDoc);
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('mousedown', onDoc);
      document.removeEventListener('keydown', onKey);
    };
  }, [open]);

  // ST-77 (bug report #116): the capo moves the SHAPES, not the key — displayed chords = chart
  // shifted by (transpose − capoFret), sounding key = chart key + transpose. Moving the capo
  // keeps "Key of G" and changes the grips; transpose moves both. The trigger reads
  // "Capo {capoFret} · Key of {chart key + transpose}".
  const soundingKey = effectiveKey(songKey, transpose);
  const neutral = capoFret === 0 && transpose === 0;
  const label = neutral
    ? (songKey ? `Key of ${songKey}` : 'As written')
    : capoFret === 0
      ? (soundingKey ? `Key of ${soundingKey}` : formatTranspose(transpose))
      : soundingKey
        ? `Capo ${capoFret} · Key of ${soundingKey}`
        : transpose !== 0
          ? `Capo ${capoFret} · ${formatTranspose(transpose)}`
          : `Capo ${capoFret}`;

  return (
    <div className={styles.wrap} ref={ref}>
      <button
        type="button"
        className={`${ui.btn} ${styles.trigger} ${open ? styles.open : ''}`}
        aria-haspopup="dialog"
        aria-expanded={open}
        aria-label={label}
        title="Capo, transpose and the key you will hear"
        onClick={() => setOpen((v) => !v)}
      >
        <GuitarIcon size={16} />
        {/* ST-84 polish: phones show the static "Key & Capo" label (the live
            capo/key reading already rides the top bar chip) — the full label
            keeps the bar to one row on desktop. aria-label keeps the full
            announcement everywhere. NOTE: the class names must NOT contain
            "trigger"/"btn" — the dock sizes its controls with descendant
            [class*="..."] rules and would style the spans (62px bug). */}
        <span className={styles.keyLabel}>{label}</span>
        <span className={styles.keyLabelShort} aria-hidden="true">
          Key &amp; Capo
        </span>
      </button>
      {open && (
        <div
          ref={dialogRef}
          tabIndex={-1}
          className={`${styles.pop} ${placement === 'up' ? styles.popUp : ''}`}
          role="dialog"
          aria-label="Key and capo"
          onClick={(e) => e.stopPropagation()}
        >
          {/* Bug report #96: the "saved with the song" / "this session" captions read as
              context-free jargon — the steppers say what they are; CapoControl's tooltip
              explains what a capo does. */}
          <div className={styles.row}>
            <CapoControl value={capoFret} onCommit={onCommitCapo} disabled={disabled} />
          </div>
          <div className={styles.row}>
            <TransposeControl songKey={songKey} capoFret={capoFret} persist={persist} />
          </div>
          <div className={styles.result}>
            <span className={ui.muted}>Key of</span>
            <strong className={styles.soundKey}>
              {soundingKey ?? formatTranspose(transpose)}
            </strong>
            {transpose !== 0 && (
              <button
                type="button"
                className={`${ui.btn} ${ui.small}`}
                onClick={() => (persist ? setTranspose(0) : setTransient(0))}
              >
                Reset
              </button>
            )}
          </div>
        </div>
      )}
    </div>
  );
}
