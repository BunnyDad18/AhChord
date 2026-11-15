import {
  Suspense,
  lazy,
  useId,
  useLayoutEffect,
  useEffect,
  useRef,
  useState,
  useCallback,
} from 'react';
import styles from './ChordName.module.css';

// ST-28: chord data + diagrams are a separate chunk, loaded with the first
// opened tooltip (not in the initial bundle).
const ChordTip = lazy(() => import('./ChordTip'));

interface ChordNameProps {
  name: string;
  /** ST-75: legend rendering — no chart-baseline lift, inherits the row font. */
  flat?: boolean;
}

/**
 * Chord token with a chord-diagram tooltip (ST-20 guitar + ST-21 piano).
 * Opens on mouseenter/focus; hides on mouseleave/blur/Esc (150 ms fade),
 * positioned above the token and clamped to the viewport.
 * ST-75: a tap/click PINS the tooltip — it stays through mouseleave/blur until
 * the chord is tapped again, Escape, or a tap outside (mid-song, the 150 ms
 * hover-close timer used to make diagrams vanish while strumming).
 * `aria-describedby` exposes the note list to screen readers (ST-27).
 */
export function ChordName({ name, flat = false }: ChordNameProps) {
  const [open, setOpen] = useState(false);
  const [pinned, setPinned] = useState(false);
  const [pos, setPos] = useState<{ top: number; left: number } | null>(null);
  const btnRef = useRef<HTMLButtonElement>(null);
  const tipRef = useRef<HTMLDivElement>(null);
  const closeTimer = useRef<number | null>(null);

  // ST-64: useId keeps the aria-describedby target unique — the old name-derived id
  // collided whenever two "C" (etc.) tokens were on screen at once (a duplicate DOM id).
  const tipId = `${useId()}-chord-tip`;

  const cancelClose = useCallback(() => {
    if (closeTimer.current !== null) {
      window.clearTimeout(closeTimer.current);
      closeTimer.current = null;
    }
  }, []);

  const scheduleClose = useCallback(() => {
    // ST-75: a pinned tip ignores hover/blur dismissal.
    if (pinned) return;
    if (closeTimer.current !== null) window.clearTimeout(closeTimer.current);
    closeTimer.current = window.setTimeout(() => setOpen(false), 150);
  }, [pinned]);

  // ST-64: drop the pending close on unmount — it used to fire setOpen post-unmount
  // (leak on mid-hover song changes).
  useEffect(() => cancelClose, [cancelClose]);

  const openTip = useCallback(() => {
    cancelClose();
    setOpen(true);
  }, [cancelClose]);

  const close = useCallback(() => {
    cancelClose();
    setPinned(false);
    setOpen(false);
  }, [cancelClose]);

  // ST-75: while pinned, a press outside the chord + tooltip dismisses it.
  useEffect(() => {
    if (!pinned) return;
    const onDoc = (e: PointerEvent) => {
      const t = e.target as Node | null;
      if (btnRef.current?.contains(t ?? null) || tipRef.current?.contains(t ?? null)) return;
      close();
    };
    document.addEventListener('pointerdown', onDoc);
    return () => document.removeEventListener('pointerdown', onDoc);
  }, [pinned, close]);

  // Position after the tooltip is measurable; clamp to the viewport.
  useLayoutEffect(() => {
    if (!open) return;
    const btn = btnRef.current?.getBoundingClientRect();
    const tip = tipRef.current?.getBoundingClientRect();
    if (!btn || !tip) return;
    const left = Math.max(
      8,
      Math.min(btn.left + btn.width / 2 - tip.width / 2, window.innerWidth - tip.width - 8),
    );
    let top = btn.top - tip.height - 10;
    if (top < 8) top = btn.bottom + 10;
    setPos({ top, left });
  }, [open]);

  const handleBlur = (e: React.FocusEvent<HTMLButtonElement>) => {
    const related = e.relatedTarget as Node | null;
    if (tipRef.current && related && tipRef.current.contains(related)) return;
    scheduleClose();
  };

  return (
    <>
      <button
        ref={btnRef}
        type="button"
        className={`${styles.chordBtn} ${pinned ? styles.pinned : ''} ${flat ? styles.flat : ''}`}
        aria-describedby={open ? tipId : undefined}
        aria-expanded={open}
        onMouseEnter={openTip}
        onMouseLeave={scheduleClose}
        onFocus={openTip}
        onBlur={handleBlur}
        onClick={() => {
          // ST-75: tap pins; tapping the same chord again releases + closes.
          if (pinned) close();
          else {
            cancelClose();
            setPinned(true);
            setOpen(true);
          }
        }}
        onKeyDown={(e) => {
          if (e.key === 'Escape') {
            close();
            e.stopPropagation(); // ST-75 review: keep Esc out of the player's exit handler
          }
        }}
      >
        {name}
      </button>

      {open && (
        <div
          ref={tipRef}
          id={tipId}
          role="tooltip"
          className={`${styles.tooltip} ${pos ? styles.visible : ''} ${pinned ? styles.pinned : ''}`}
          style={
            pos
              ? { position: 'fixed', top: pos.top, left: pos.left }
              : { position: 'fixed', top: -9999, left: -9999 }
          }
          onMouseEnter={cancelClose}
          onMouseLeave={scheduleClose}
          onKeyDown={(e) => {
            if (e.key === 'Escape') {
              close();
              e.stopPropagation(); // ST-75 review: pinned tips must not exit the player
              btnRef.current?.focus();
            }
          }}
        >
          <Suspense fallback={<div className={styles.notes}>…</div>}>
            <ChordTip name={name} />
          </Suspense>
        </div>
      )}
    </>
  );
}
