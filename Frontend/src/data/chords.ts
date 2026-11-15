/**
 * Guitar chord shape database (ST-20).
 *
 * A shape is a fixed fingering. `frets` is 6 values (low E -> high e):
 *   -1 = muted, 0 = open, 1+ = absolute fret number.
 * `fingers` is the finger used on each string (0 = none, 1 = index ... 4 = pinky).
 * `barre` (when present) is the index-finger barre: the fret it sits on and the
 * inclusive string range (0-based) it covers.
 *
 * Keyed by SOUNDING chord name (chords arrive transposed by ST-08). Lookups that
 * miss this table fall back to the piano tooltip (ST-21) and never error.
 */

export interface ChordShape {
  name: string;
  frets: number[];
  fingers: number[];
  barre?: { fret: number; from: number; to: number };
}

const S = (
  name: string,
  frets: number[],
  fingers: number[],
  barre?: { fret: number; from: number; to: number },
): ChordShape => ({ name, frets, fingers, barre });

/** All shapes, keyed by a normalized name (root upper, accidentals as #/b). */
const SHAPES: Record<string, ChordShape> = {
  // ---------- Open majors ----------
  C: S('C', [-1, 3, 2, 0, 1, 0], [0, 2, 1, 0, 3, 0]),
  D: S('D', [-1, -1, 0, 2, 3, 2], [0, 0, 0, 1, 2, 3]),
  E: S('E', [0, 2, 2, 1, 0, 0], [0, 2, 1, 3, 0, 0]),
  F: S('F', [1, 3, 3, 2, 1, 1], [1, 1, 1, 1, 1, 1], { fret: 1, from: 0, to: 5 }),
  G: S('G', [3, 2, 0, 0, 0, 3], [0, 2, 0, 0, 0, 3]),
  A: S('A', [0, 0, 2, 2, 2, 0], [0, 0, 2, 1, 3, 0]),
  B: S('B', [7, 9, 9, 8, 7, 7], [1, 1, 1, 1, 1, 1], { fret: 7, from: 0, to: 5 }),

  // ---------- Open minors ----------
  Am: S('Am', [0, 0, 2, 2, 1, 0], [0, 0, 2, 1, 3, 0]),
  Dm: S('Dm', [-1, -1, 0, 2, 3, 1], [0, 0, 0, 1, 2, 3]),
  Em: S('Em', [0, 2, 2, 0, 0, 0], [0, 2, 1, 0, 0, 0]),
  Fm: S('Fm', [1, 3, 3, 1, 1, 1], [1, 1, 1, 1, 1, 1], { fret: 1, from: 0, to: 5 }),
  Gm: S('Gm', [3, 5, 5, 3, 3, 3], [1, 1, 1, 1, 1, 1], { fret: 3, from: 0, to: 5 }),
  Bb: S('Bb', [6, 8, 8, 6, 6, 6], [1, 1, 1, 1, 1, 1], { fret: 6, from: 0, to: 5 }),
  Bm: S('Bm', [2, 2, 4, 4, 3, 2], [1, 1, 1, 1, 1, 1], { fret: 2, from: 0, to: 5 }),
  Csm: S('C#m', [4, 4, 6, 6, 5, 4], [1, 1, 1, 1, 1, 1], { fret: 4, from: 0, to: 5 }),

  // ---------- Dominant 7ths ----------
  C7: S('C7', [-1, 3, 3, 2, 1, 0], [0, 2, 3, 1, 4, 0]),
  D7: S('D7', [-1, -1, 0, 2, 1, 0], [0, 0, 0, 2, 1, 0]),
  E7: S('E7', [0, 2, 0, 1, 3, 0], [0, 2, 0, 1, 3, 0]),
  A7: S('A7', [0, 0, 0, 2, 0, 0], [0, 0, 0, 1, 0, 0]),
  B7: S('B7', [2, 1, 2, 0, 2, 0], [0, 2, 1, 0, 3, 0]),
  G7: S('G7', [3, 2, 0, 0, 0, 1], [0, 2, 0, 0, 0, 3]),
  Em7: S('Em7', [0, 2, 2, 0, 3, 0], [0, 2, 1, 0, 3, 0]),
  Am7: S('Am7', [0, 0, 1, 0, 0, 0], [0, 0, 1, 0, 0, 0]),
  Dm7: S('Dm7', [-1, -1, 0, 2, 1, 1], [0, 0, 0, 2, 1, 3]),

  // ---------- Major 7ths ----------
  Cmaj7: S('Cmaj7', [-1, 3, 2, 0, 0, 0], [0, 2, 1, 0, 0, 0]),
  Dmaj7: S('Dmaj7', [-1, -1, 0, 2, 2, 2], [0, 0, 0, 1, 2, 3]),
  Emaj7: S('Emaj7', [0, 2, 1, 1, 0, 0], [0, 2, 1, 3, 0, 0]),
  Gmaj7: S('Gmaj7', [3, 2, 0, 0, 0, 2], [0, 2, 0, 0, 0, 3]),
  Amaj7: S('Amaj7', [0, 0, 2, 1, 2, 0], [0, 0, 2, 1, 3, 0]),

  // ---------- sus / dim / aug ----------
  Csus4: S('Csus4', [-1, 3, 5, 3, 0, 0], [0, 2, 3, 1, 0, 0]),
  Dsus4: S('Dsus4', [-1, -1, 0, 2, 2, 2], [0, 0, 0, 1, 2, 3]),
  Gsus4: S('Gsus4', [-1, 3, 0, 0, 0, 3], [0, 2, 0, 0, 0, 3]),
  Cdim: S('Cdim', [-1, 3, 1, 3, 0, 0], [0, 2, 1, 3, 0, 0]),
  Ddim: S('Ddim', [-1, 7, 3, 4, -1, -1], [0, 3, 1, 2, 0, 0]),
  Caug: S('Caug', [-1, 3, 1, -1, -1, 0], [0, 2, 1, 0, 0, 0]),
  Daug: S('Daug', [-1, 4, 0, 0, -1, 6], [0, 1, 0, 0, 0, 3]),

  // ---------- Common slash chords ----------
  'G/B': S('G/B', [-1, 2, 0, 0, 0, 3], [0, 1, 0, 0, 0, 2]),
  'C/G': S('C/G', [-1, 3, 5, 0, 0, 0], [0, 2, 3, 0, 0, 0]),
  'D/F#': S('D/F#', [-1, 4, 0, 2, 0, 0], [0, 1, 0, 2, 0, 0]),
  'A/C#': S('A/C#', [-1, 0, 1, 2, 0, 0], [0, 0, 1, 2, 0, 0]),
  'E/B': S('E/B', [-1, 0, 2, 0, 0, 0], [0, 0, 1, 0, 0, 0]),
  'F#m/B': S('F#m/B', [-1, 4, 2, 0, 0, 0], [0, 2, 1, 0, 0, 0]),
  'Bm/E': S('Bm/E', [0, 2, 1, 1, 0, 0], [0, 2, 1, 3, 0, 0]),
  'F/G': S('F/G', [-1, 3, 5, 0, 0, 3], [0, 2, 3, 0, 0, 1]),
};

/** Enharmonic aliases so a transposed name still resolves (b <-> #). */
const ALIASES: Record<string, string> = {
  Fb: 'E',
  'Cb': 'B',
  'E#': 'F',
  'B#': 'C',
  Gb: 'F#',
  Db: 'C#',
  Ab: 'G#',
  Eb: 'D#',
  'Bbm': 'A#m',
  'F#m': 'F#m',
};

/** Normalize a chord name for lookup (trim, uppercase root, unify accidentals). */
export function normalizeChordName(name: string): string {
  const trimmed = name.trim();
  if (trimmed.length === 0) return trimmed;
  // Uppercase the root letter only; keep the rest as-is for quality matching.
  const upper = trimmed.charAt(0).toUpperCase() + trimmed.slice(1);
  const aliased = ALIASES[upper];
  return aliased ?? upper;
}

/**
 * Resolve a chord name to a shape. Returns null when the chord has no known
 * shape — callers render the chord normally (no tooltip) rather than erroring.
 */
export function lookupChord(name: string): ChordShape | null {
  return SHAPES[normalizeChordName(name)] ?? null;
}

/** Every shape in the table (ST-59: window-geometry tests iterate the real library). */
export function allShapes(): ChordShape[] {
  return Object.values(SHAPES);
}

/** The set of chord names that have a guitar shape (used by the tooltip toggle). */
export function hasGuitarShape(name: string): boolean {
  return lookupChord(name) !== null;
}
