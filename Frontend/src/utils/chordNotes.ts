/**
 * Chord -> note-name derivation (ST-21).
 * Parse root + quality (+ optional /bass) into a set of semitone offsets from
 * the root, then emit absolute note names. Slash chords put the bass note first
 * (it becomes the leftmost highlighted piano key).
 */

const NOTE_NAMES = ['C', 'C#', 'D', 'D#', 'E', 'F', 'F#', 'G', 'G#', 'A', 'A#', 'B'];

const ENHARMONIC: Record<string, string> = {
  Db: 'C#',
  Eb: 'D#',
  Gb: 'F#',
  Ab: 'G#',
  Bb: 'A#',
  Cb: 'B',
  Fb: 'E',
  'E#': 'F',
  'B#': 'C',
};

/** Interval sets per quality (semitones above the root). */
const QUALITY_INTERVALS: Record<string, number[]> = {
  '': [0, 4, 7], // major triad
  m: [0, 3, 7],
  7: [0, 4, 7, 10],
  maj7: [0, 4, 7, 11],
  m7: [0, 3, 7, 10],
  'm7b5': [0, 3, 6, 10],
  9: [0, 4, 7, 10, 14],
  sus2: [0, 2, 7],
  sus4: [0, 5, 7],
  dim: [0, 3, 6],
  aug: [0, 4, 8],
};

/** Normalize an accidental so lookup is consistent (b -> # where sensible). */
function normalizeNote(note: string): string {
  return ENHARMONIC[note] ?? note;
}

function noteToIndex(note: string): number {
  const n = normalizeNote(note);
  const idx = NOTE_NAMES.indexOf(n);
  if (idx >= 0) return idx;
  // Last resort: first letter.
  return NOTE_NAMES.indexOf(n.charAt(0)) === -1 ? 0 : NOTE_NAMES.indexOf(n.charAt(0));
}

export interface ParsedChordName {
  root: string;
  quality: string;
  bass: string | null;
}

/** Splits "C#m7/G" into { root: 'C#', quality: 'm7', bass: 'G' }. */
export function parseChordName(name: string): ParsedChordName {
  const trimmed = name.trim();
  const slashIdx = trimmed.indexOf('/');
  let main = trimmed;
  let bass: string | null = null;
  if (slashIdx > 0) {
    main = trimmed.slice(0, slashIdx);
    bass = trimmed.slice(slashIdx + 1);
  }

  const rootMatch = main.match(/^([A-G])([#b]?)/);
  if (!rootMatch) {
    return { root: main.toUpperCase().charAt(0) || 'C', quality: '', bass };
  }
  const root = rootMatch[1] + (rootMatch[2] ?? '');
  const quality = main.slice(root.length);
  return { root, quality, bass };
}

/**
 * Returns the chord's note names. Slash chords lead with the bass note, then
 * the remaining chord tones (de-duplicated). Unknown qualities fall back to a
 * major triad (best effort per ST-21).
 */
export function chordNotes(name: string): string[] {
  const { root, quality, bass } = parseChordName(name);
  const rootIdx = noteToIndex(root);

  const intervals = QUALITY_INTERVALS[quality] ?? QUALITY_INTERVALS[''];
  const notes = Array.from(
    new Set(intervals.map((iv) => NOTE_NAMES[(rootIdx + iv) % 12])),
  );

  if (bass) {
    const bassNote = NOTE_NAMES[noteToIndex(bass)];
    const rest = notes.filter((n) => n !== bassNote);
    return [bassNote, ...rest];
  }
  return notes;
}
