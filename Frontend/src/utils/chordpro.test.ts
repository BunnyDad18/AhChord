import { describe, expect, it } from 'vitest';
import { parseChordPro } from './chordpro';

/** Parses a single lyric line and returns its first (only) parsed line. */
function parseLine(input: string) {
  const parsed = parseChordPro(input);
  return parsed.sections[0].lines[0];
}

describe('parseChordPro — bar notation (ST-54)', () => {
  it('classifies [|] as a bar', () => {
    const line = parseLine('[|]');
    expect(line.bars).toEqual([{ kind: 'bar', charIndex: 0 }]);
    expect(line.chords).toHaveLength(0);
    expect(line.annotations).toHaveLength(0);
  });

  it('classifies [||:] as repeat-start', () => {
    const line = parseLine('[||:]');
    expect(line.bars).toEqual([{ kind: 'repeat-start', charIndex: 0 }]);
  });

  it('classifies [:||] as repeat-end', () => {
    const line = parseLine('[:||]');
    expect(line.bars).toEqual([{ kind: 'repeat-end', charIndex: 0 }]);
  });

  it('parses the bug-report mixed line with correct ordering and charIndexes', () => {
    // [||:] [G] [|] [F] [|] [C] [|] [:||]
    const text = '[||:] [G] [|] [F] [|] [C] [|] [:||]';
    const line = parseLine(text);

    expect(line.text).toBe(text); // display-only: the stored text is never mutated
    expect(line.chords.map((c) => c.name)).toEqual(['G', 'F', 'C']);
    expect(line.chords.map((c) => c.charIndex)).toEqual([6, 14, 22]);
    expect(line.bars).toEqual([
      { kind: 'repeat-start', charIndex: 0 },
      { kind: 'bar', charIndex: 10 },
      { kind: 'bar', charIndex: 18 },
      { kind: 'bar', charIndex: 26 },
      { kind: 'repeat-end', charIndex: 30 },
    ]);
  });

  it('keeps bars, chords and annotations in one line without crosstalk', () => {
    const text = '[||:] [C] [*rit.] [|] sing';
    const line = parseLine(text);
    expect(line.chords).toEqual([{ name: 'C', charIndex: 6 }]);
    expect(line.annotations).toEqual([{ text: '*rit.', charIndex: 10 }]);
    expect(line.bars).toEqual([
      { kind: 'repeat-start', charIndex: 0 },
      { kind: 'bar', charIndex: 18 },
    ]);
  });

  it('still drops unknown bracket tokens (today behavior)', () => {
    const line = parseLine('[foo] [C] bar');
    expect(line.chords).toEqual([{ name: 'C', charIndex: 6 }]);
    expect(line.bars).toHaveLength(0);
    expect(line.annotations).toHaveLength(0);
  });

  it('does not treat near-miss bar tokens as bars', () => {
    for (const content of ['||', '|:', ':|', '|| :', '| |']) {
      const line = parseLine(`[${content}] [C] x`);
      expect(line.bars, `[${content}]`).toHaveLength(0);
      expect(line.chords).toEqual([{ name: 'C', charIndex: content.length + 3 }]);
    }
  });

  it('leaves lines without bar notation with an empty bars array', () => {
    const line = parseLine('[C] hello [G] world');
    expect(line.bars).toEqual([]);
    expect(line.chords.map((c) => c.name)).toEqual(['C', 'G']);
  });
});

// ST-60: grammar parity with WebApi/Services/ChordProParser.cs — every server finding from the
// code review has the same observable behavior client-side (editor/import previews).
describe('parseChordPro — ST-60 parser hardening parity', () => {
  it('keeps colon-in-label section directives (server finding 1)', () => {
    const p = parseChordPro('{start_of_verse label="Verse: 1"}\nHello [C] world\n{end_of_verse}');
    expect(p.sections).toHaveLength(1);
    expect(p.sections[0].type).toBe('verse');
    expect(p.sections[0].label).toBe('Verse: 1');
    expect(p.sections[0].lines).toHaveLength(1);
  });

  it('recognizes widened chord qualities (server finding 2)', () => {
    const cases: [string, string][] = [
      ['[C add9]', 'C add9'],
      ['[Em7-5]', 'Em7-5'],
      ['[F#m7(b5)]', 'F#m7(b5)'],
      ['[Gm7(add9)]', 'Gm7(add9)'],
      ['[D7sus4]', 'D7sus4'],
      ['[C/Gsus4]', 'C/Gsus4'],
    ];
    for (const [token, name] of cases) {
      expect(
        parseLine(`${token} lyric`).chords.map((c) => c.name),
        token,
      ).toEqual([name]);
    }
  });

  it('rejects double accidentals (server finding 12)', () => {
    for (const token of ['[Bbb]', '[Cbb]', '[F##]']) {
      expect(parseLine(`${token} lyric`).chords, token).toHaveLength(0);
    }
  });

  it('parses legacy shorthand headers directly (server finding 3)', () => {
    const p = parseChordPro('{t:Africa}\n{a: Toto}\n{k: E minor}\n{c: practice version}');
    expect(p.header?.title).toBe('Africa');
    expect(p.header?.artist).toBe('Toto');
    expect(p.header?.key).toBe('E minor');
    expect(p.sections[0].label).toBe('practice version');
  });

  it('never titles a song with a tempo form (server findings 3/5)', () => {
    for (const d of ['{t:4/4}', '{t:120 bpm}', '{t:120}']) {
      expect(parseChordPro(d).header?.title, d).toBeUndefined();
    }
  });

  it('reads {C: 3} as capo, not a comment (server finding 4)', () => {
    const p = parseChordPro('{C: 3}');
    expect(p.header?.capo).toBe('3');
    expect(p.sections).toHaveLength(0);
  });

  it('closes sections on {eoc}/{eov}/{eob} (server finding 6)', () => {
    const p = parseChordPro('{soc}\nChorus line [C]\n{eoc}\nAfter the chorus [G]');
    expect(p.sections).toHaveLength(2);
    expect(p.sections[0].type).toBe('chorus');
    expect(p.sections[0].lines).toHaveLength(1);
    expect(p.sections[1].type).toBe('other');
    expect(p.sections[1].lines).toHaveLength(1);
  });

  it('accepts {sov}/{soc}/{sob} starts (server parity)', () => {
    const p = parseChordPro('{sov}\nA [C]\n{soc}\nB [G]\n{sob}\nC [D]');
    expect(p.sections.map((s) => s.type)).toEqual(['verse', 'chorus', 'bridge']);
  });

  it('honors escaped brackets, unescaped content (server finding 8)', () => {
    const line = parseLine('sing [*a\\]b] here');
    expect(line.annotations).toEqual([{ text: '*a]b', charIndex: 5 }]);
  });

  it('joins directives spanning two lines (server finding 9)', () => {
    const p = parseChordPro('{comment: A\nB}');
    expect(p.sections).toHaveLength(1);
    expect(p.sections[0].label).toBe('A B');
    expect(p.sections[0].lines).toHaveLength(0);
  });

  it('matches wide bare headings (server finding 11)', () => {
    expect(parseChordPro('Second Final Chorus').sections[0].label).toBe('Second Final Chorus');
    // Numbered variants normalize into typed sections with the number in the label.
    const p = parseChordPro('Chorus 2');
    expect(p.sections[0].type).toBe('chorus');
    expect(p.sections[0].label).toBe('Chorus 2');
    // Plain lyric lines stay lyric.
    expect(parseChordPro('We sing choruses').sections[0].lines).toHaveLength(1);
  });

  it('extracts labels quote-aware (server finding 14)', () => {
    const p = parseChordPro('{start_of_verse label="A" extra="B"}');
    expect(p.sections[0].label).toBe('A');
  });

  it('empty directive values yield no blank header fields (server finding 15)', () => {
    const p = parseChordPro('{title:}\n{meta: }');
    expect(p.header?.title).toBeUndefined();
    expect(p.header?.meta).toHaveLength(0);
  });

  it('warns on unrecognized section directives (server finding 1)', () => {
    const p = parseChordPro('{start_of_verse}\nFirst [C]\n{start_of_solo}\nSolo line [G]');
    expect(p.warnings.some((w) => w.includes('start_of_solo'))).toBe(true);
    expect(p.sections).toHaveLength(1);
    expect(p.sections[0].lines).toHaveLength(2);
  });

  it('captures genre and year headers (parser parity)', () => {
    const p = parseChordPro('{genre: rock}\n{year: 1969}');
    expect(p.header?.genre).toBe('rock');
    expect(p.header?.year).toBe('1969');
  });
});

describe('parseChordPro — double-bracket chords (report 2026-11-12)', () => {
  it('reads [[G]] as a chord, not as brackets around one', () => {
    // The "On Facedown there is a [[G]]" echo: BRACKET matched only the inner [G],
    // so the outer brackets rendered as literal lyric text.
    const line = parseLine('[[G]] Facedown [[Am7]] low');
    expect(line.chords.map((c) => c.name)).toEqual(['G', 'Am7']);
    expect(line.text).toBe('[G] Facedown [Am7] low');
  });

  it('leaves single-bracket lines untouched', () => {
    const line = parseLine('[G] Facedown [Am7] low');
    expect(line.chords.map((c) => c.name)).toEqual(['G', 'Am7']);
    expect(line.text).toBe('[G] Facedown [Am7] low');
  });
});
