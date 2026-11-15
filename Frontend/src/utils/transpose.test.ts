import { describe, expect, it } from 'vitest';
import { transposeChord, transposeParsed } from './transpose';

// MT-04: client mirror of ChordTransposer.cs — offline transpose must match the server.
describe('transposeChord', () => {
  it('shifts plain triads by semitones, normalizing to sharps', () => {
    expect(transposeChord('C', 2)).toBe('D');
    expect(transposeChord('A', 3)).toBe('C');
    expect(transposeChord('Eb', 1)).toBe('E');
    expect(transposeChord('Bb', -2)).toBe('G#'); // sharp-preferred, same as the server
    expect(transposeChord('C', -1)).toBe('B');
    expect(transposeChord('Cb', 0)).toBe('B'); // normalizes like the server (pitch class 11)
  });

  it('preserves the quality suffix verbatim', () => {
    expect(transposeChord('Am7', 2)).toBe('Bm7');
    expect(transposeChord('C add9', 1)).toBe('C# add9');
    expect(transposeChord('F#m7(b5)', -3)).toBe('D#m7(b5)');
    expect(transposeChord('Gsus4', 5)).toBe('Csus4');
  });

  it('transposes slash chords on both sides', () => {
    expect(transposeChord('C/G', 2)).toBe('D/A');
    expect(transposeChord('C/Gsus4', 1)).toBe('C#/G#sus4'); // bass transposes too, its quality preserved
  });

  it('never throws on junk', () => {
    expect(transposeChord('', 3)).toBe('');
    expect(transposeChord('Nonsense', 3)).toBe('Nonsense');
    expect(transposeChord('Bbb', 2)).toBe('Bbb');
  });
});

describe('transposeParsed (text + index rewrite)', () => {
  const line = (text: string, chords: Array<[string, number]>) => ({
    label: null as string | null,
    type: 'verse' as const,
    lines: [
      {
        text,
        chords: chords.map(([name, charIndex]) => ({ name, charIndex })),
        annotations: [] as Array<{ text: string; charIndex: number }>,
        bars: [] as Array<{ kind: 'bar' | 'repeat-start' | 'repeat-end'; charIndex: number }>,
      },
    ],
  });

  it('rewrites bracketed text so chords and indexes stay in sync when names grow', () => {
    // [C] grows to [C#] — the ']' would shift the slice if indexes weren't recomputed.
    const parsed = { header: null, warnings: [], sections: [line('Wandering [C] by the sea', [['C', 10]])] as any };
    transposeParsed(parsed as any, 1);
    const l = parsed.sections[0].lines[0];
    expect(l.text).toBe('Wandering [C#] by the sea');
    expect(l.chords[0]).toEqual({ name: 'C#', charIndex: 10 });
  });

  it('applies every chord on the line in order', () => {
    const parsed = { header: null, warnings: [], sections: [line('[Am] [F] [C] [G]', [['Am', 0], ['F', 5], ['C', 9], ['G', 13]])] as any };
    transposeParsed(parsed as any, -2);
    const l = parsed.sections[0].lines[0];
    expect(l.text).toBe('[Gm] [D#] [A#] [F]');
    expect(l.chords.map((c: { name: string }) => c.name)).toEqual(['Gm', 'D#', 'A#', 'F']);
    // [F]→[D#] grows, so the following tokens shift +1 in the rewritten text (server parity).
    expect(l.chords.map((c: { charIndex: number }) => c.charIndex)).toEqual([0, 5, 10, 15]);
  });

  it('shifts annotation indexes past chords that changed length', () => {
    const sections = [
      {
        label: null,
        type: 'verse' as const,
        lines: [
          {
            text: '[C] hello',
            chords: [{ name: 'C', charIndex: 0 }],
            annotations: [{ text: 'note', charIndex: 7 }], // after the chord token
            bars: [],
          },
        ],
      },
    ];
    transposeParsed({ header: null, warnings: [], sections } as any, 1);
    const l = sections[0].lines[0];
    expect(l.text).toBe('[C#] hello');
    expect(l.annotations[0].charIndex).toBe(8); // +1 for the grown name
  });
});
