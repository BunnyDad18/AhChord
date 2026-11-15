import { useTransposeStore } from '../stores/transpose';
import styles from './TransposeControl.module.css';

/** Musically conventional spellings, index = semitones above C. */
const KEYS = ['C', 'C#', 'D', 'Eb', 'E', 'F', 'F#', 'G', 'Ab', 'A', 'Bb', 'B'];

const KEY_INDEX: Record<string, number> = {
  C: 0, 'C#': 1, Db: 1, D: 2, 'D#': 3, Eb: 3, E: 4, F: 5,
  'F#': 6, Gb: 6, G: 7, 'G#': 8, Ab: 8, A: 9, 'A#': 10, Bb: 10, B: 11,
};

/** Root note of a key name — "Em" → E, "Fm7" → F, "C#" → C# (stored keys carry qualities). */
export function keyIndex(key: string | null | undefined): number | null {
  if (!key) return null;
  const m = key.trim().match(/^([A-G][#b]?)/);
  if (!m) return null;
  const i = KEY_INDEX[m[1]];
  return i === undefined ? null : i;
}

/** The effective key when the song is transposed (null without a stored key). */
export function effectiveKey(songKey: string | null | undefined, transpose: number): string | null {
  const base = keyIndex(songKey);
  if (base === null) return null;
  return KEYS[(((base + transpose) % 12) + 12) % 12];
}

export function formatTranspose(t: number): string {
  return t > 0 ? `+${t}` : `${t}`;
}

interface TransposeControlProps {
  /** The song's stored key (e.g. "G") — the basis for the key dropdown. */
  songKey?: string | null;
  /** The song's stored capo fret — ST-77: it shifts the displayed shapes (projection),
   * never the key; kept on the props for the callers' signature, not used in the key math. */
  capoFret?: number;
  /** Compact variant for tight headers (player): stepper only, no key dropdown. */
  size?: 'sm' | 'md';
  /** Persist changes to the store's current songId (default true). ST-55: the setlist player
   * passes false — the session is the source of truth, and the store's songId belongs to an
   * unrelated song there. */
  persist?: boolean;
}

/**
 * Transpose control (ST-39 E1/E2 — replaces CapoSelector). A − / value / + stepper over
 * -12…+12 semitones plus a "play in key" dropdown that sets the transpose from the stored
 * key. Session state only — the stored chart is never mutated. ST-77 (bug report #116):
 * transpose moves BOTH the shapes and the key (chart key + transpose); the saved capo
 * (capoFret) only shifts the displayed shapes down and is applied by the projection.
 */
export function TransposeControl({ songKey, size = 'md', persist = true }: TransposeControlProps) {
  const transpose = useTransposeStore((s) => s.transpose);
  const setTransient = useTransposeStore((s) => s.setTransient);
  const setTranspose = useTransposeStore((s) => s.setTranspose);
  const bump = useTransposeStore((s) => s.bump);

  const baseIdx = keyIndex(songKey);
  // ST-77 (#116): the key = chart key + transpose. The capo keeps the key and moves the
  // shapes down, so it never enters the key math.
  const effKey = effectiveKey(songKey, transpose);
  // ST-55: setlist mode (persist=false) keeps the value session-only.
  const apply = persist ? setTranspose : setTransient;

  return (
    <div className={`${styles.group} ${size === 'sm' ? styles.sm : ''}`} role="group" aria-label="Transpose and key">
      <span className={styles.label} aria-hidden="true">
        Transpose
      </span>
      {/* ST-39 E2: stepper UI — minus / value / plus. */}
      <button
        type="button"
        className={styles.stepBtn}
        onClick={() => bump(-1, !persist)}
        disabled={transpose <= -12}
        aria-label="Transpose down one semitone"
      >
        −
      </button>
      <span className={styles.value} title={transpose === 0 ? 'As written' : `Transposed ${formatTranspose(transpose)} semitones`}>
        {formatTranspose(transpose)}
      </span>
      <button
        type="button"
        className={styles.stepBtn}
        onClick={() => bump(1, !persist)}
        disabled={transpose >= 12}
        aria-label="Transpose up one semitone"
      >
        +
      </button>

      {/* ST-39 E2: pick the key to play in — sets the transpose from the stored key. */}
      {size === 'md' && baseIdx !== null && (
        <select
          className={styles.keySelect}
          value={effKey ?? ''}
          onChange={(e) => {
            const target = KEY_INDEX[e.target.value];
            // Pick the key to hear: ST-77 (#116) — sounding = chart key + transpose,
            // so the session adjustment closes the gap (target − chart key); the capo
            // keeps the key, so it stays out of the math.
            if (target !== undefined && baseIdx !== null) apply(target - baseIdx);
          }}
          aria-label="Play in key"
        >
          {KEYS.map((k) => (
            <option key={k} value={k}>
              {k}
            </option>
          ))}
        </select>
      )}
    </div>
  );
}
