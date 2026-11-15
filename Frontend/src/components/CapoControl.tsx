import styles from './CapoControl.module.css';

interface CapoControlProps {
  /** The song's stored capo fret, 0–6 (server is the source of truth). */
  value: number;
  /** Called with the new fret when the user steps — the page persists it via PUT. */
  onCommit: (fret: number) => void;
  disabled?: boolean;
  /** Compact variant for tight headers (player): stepper only, no label. */
  size?: 'sm' | 'md';
}

/**
 * Capo control (ST-51 #8 / ST-76 #94 / ST-77 #116). A − / value / + stepper over frets 0–6
 * that writes the song's stored CapoFret. The capo moves the SHAPES shown down (by the capo
 * amount) so the guitar with the capo on sounds the key of the chart — the key only moves
 * with the session transpose.
 */
export function CapoControl({ value, onCommit, disabled = false, size = 'md' }: CapoControlProps) {
  const step = (delta: number) => {
    const next = Math.max(0, Math.min(6, value + delta));
    if (next !== value) onCommit(next);
  };

  return (
    <div
      className={`${styles.group} ${size === 'sm' ? styles.sm : ''}`}
      role="group"
      aria-label="Capo"
      title="Capo — saved with the song. Raising it moves the chords shown down, so with the capo on you sound the song's key."
    >
      <span className={styles.label} aria-hidden="true">
        Capo
      </span>
      <button
        type="button"
        className={styles.stepBtn}
        onClick={() => step(-1)}
        disabled={disabled || value <= 0}
        aria-label="Capo down one fret"
      >
        −
      </button>
      <span className={styles.value}>{value}</span>
      <button
        type="button"
        className={styles.stepBtn}
        onClick={() => step(1)}
        disabled={disabled || value >= 6}
        aria-label="Capo up one fret"
      >
        +
      </button>
    </div>
  );
}
