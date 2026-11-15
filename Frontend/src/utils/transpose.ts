/**
 * Client-side mirror of WebApi/Services/ChordTransposer.cs (MT-04).
 *
 * Online, a transposed chart comes from GET /api/songs/{id}?capo=N — the server parses the
 * stored ChordPro and transposes by N − song.CapoFret (ST-77: the capo moves the shapes
 * down, the key is chart key + N). Offline we reproduce that projection
 * with parseChordPro (utils/chordpro.ts) + this module, so a cached chart renders identically
 * to the server-rendered one. Keep the grammar and note normalization in sync with the C# file.
 */

import type { ParsedSongLite } from './chordpro';

// Mirrors ChordProTransposer.Chord: root A–G, one accidental, quality with spaces/hyphens/
// plus/parens ([C add9], [Em7-5], [F#m7(b5)]), optional /bass (its quality has no spaces).
// (?![#b]) rejects double accidentals ([Bbb]) — they stay untouched text, not mangled.
const CHORD_RE =
  /^([A-G])(#|b)?(?![#b])([A-Za-z0-9()\-+ ]*)(?:\/([A-G])(#|b)?(?![#b])([A-Za-z0-9()\-+]*))?$/;

// Pitch class 0–11, sharp-preferred (same table as the server).
const NOTE_NAMES = ['C', 'C#', 'D', 'D#', 'E', 'F', 'F#', 'G', 'G#', 'A', 'A#', 'B'];

const ROOT_INDEX: Record<string, number> = { C: 0, D: 2, E: 4, F: 5, G: 7, A: 9, B: 11 };

function transposeNote(root: string, acc: string | undefined, semitones: number): string | null {
  const idx = ROOT_INDEX[root] ?? -1;
  if (idx < 0) return null;
  const base = idx + (acc === '#' ? 1 : acc === 'b' ? -1 : 0);
  // Reduce the shift before adding so a negative modulo can't escape (ST-60 parity).
  const target = ((base + (semitones % 12)) % 12 + 12) % 12;
  return NOTE_NAMES[target];
}

/** Transposes a single chord name by `semitones`. Invalid input is returned unchanged (never throws). */
export function transposeChord(chord: string, semitones: number): string {
  if (!chord || !chord.trim()) return chord;

  const m = CHORD_RE.exec(chord.trim());
  if (!m) return chord; // invalid → unchanged

  const quality = m[3] ?? '';
  const root = transposeNote(m[1], m[2], semitones);
  if (root === null) return chord;

  let result = root + quality;

  if (m[4] !== undefined) {
    // [C/Gsus4] style: the bass carries its own quality, preserved verbatim.
    const bass = transposeNote(m[4], m[5], semitones);
    if (bass === null) return chord;
    result += '/' + bass + (m[6] ?? '');
  }

  return result;
}

/**
 * Transposes every chord in a parsed projection in place (mirror of Transpose(ParsedSong, N)).
 * The line text is rewritten so bracketed tokens stay in sync with the renamed chord names and
 * all char indexes are recomputed — the renderer slices text at charIndex + name length, so a
 * transposed name of a different length would leave a stray ']' behind. Header untouched.
 */
export function transposeParsed(parsed: ParsedSongLite, semitones: number): ParsedSongLite {
  if (semitones === 0) return parsed;
  for (const section of parsed.sections)
    for (const line of section.lines) transposeLine(line, semitones);
  return parsed;
}

function transposeLine(line: ParsedSongLite['sections'][number]['lines'][number], semitones: number): void {
  const chords = [...line.chords].sort((a, b) => a.charIndex - b.charIndex);
  if (chords.length === 0) return;

  let out = '';
  const deltas: Array<{ start: number; delta: number }> = []; // original start → length change
  let pos = 0;
  for (const chord of chords) {
    const start = chord.charIndex; // index of '[' in the current text
    const end = start + 1 + chord.name.length + 1; // one past ']'
    if (start < pos || end > line.text.length) continue; // token not where expected — leave as is

    out += line.text.slice(pos, start);
    const newStart = out.length; // where '[' lands in the rewritten text
    const transposed = transposeChord(chord.name, semitones);
    out += `[${transposed}]`;
    if (transposed.length !== chord.name.length) deltas.push({ start, delta: transposed.length - chord.name.length });
    chord.charIndex = newStart;
    chord.name = transposed;
    pos = end;
  }
  out += line.text.slice(pos);

  // Annotations share the same text: shift each by the length changes of chords before it.
  for (const ann of line.annotations) {
    let shift = 0;
    for (const d of deltas) if (d.start < ann.charIndex) shift += d.delta;
    ann.charIndex += shift;
  }

  // ST-54: bars share the same text — same shift as annotations.
  for (const bar of line.bars) {
    let shift = 0;
    for (const d of deltas) if (d.start < bar.charIndex) shift += d.delta;
    bar.charIndex += shift;
  }

  line.text = out;
}
