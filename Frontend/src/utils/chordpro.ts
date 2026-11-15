/**
 * Client-side mirror of WebApi/Services/ChordProParser.cs (ST-07).
 * Used for instant previews in the song editor (ST-22) and import screen (ST-24)
 * so the UI never waits on the server. Keep the two in sync: same section
 * directives, same chord-name grammar, same annotations.
 */

export interface ParsedChord {
  name: string;
  charIndex: number;
}

export interface ParsedAnnotation {
  text: string;
  charIndex: number;
}

/** ST-54: ChordPro bar notation — `[|]` barline, `[||:]` repeat start, `[:||]` repeat end. */
export type BarKind = 'bar' | 'repeat-start' | 'repeat-end';

export interface ParsedBar {
  kind: BarKind;
  charIndex: number;
}

export interface ParsedLine {
  text: string;
  chords: ParsedChord[];
  annotations: ParsedAnnotation[];
  bars: ParsedBar[];
}

export interface ParsedSection {
  label: string | null;
  type: 'verse' | 'chorus' | 'bridge' | 'other';
  lines: ParsedLine[];
}

export interface ParsedHeader {
  title?: string;
  artist?: string;
  key?: string;
  capo?: string;
  time?: string;
  tag?: string;
  /** ST-35 F2 parity: genre + release year (HeaderNames on the server). */
  genre?: string;
  year?: string;
  meta: { name: string; value: string }[];
}

export interface ParsedSongLite {
  header: ParsedHeader | null;
  sections: ParsedSection[];
  /** ST-60: degradation notes, mirrors ParsedSong.Warnings (unrecognized section directives). */
  warnings: string[];
}

// ST-60: grammar identical to ChordProParser.ChordName — qualities with spaces, hyphens, plus
// and parens ([C add9], [Em7-5], [F#m7(b5)], [Gm7(add9)], [D7sus4]). The root stays strict
// (A–G + one accidental); (?![#b]) rejects double accidentals ([Bbb]) so they degrade as text.
const CHORD_NAME =
  /^[A-G](#|b)?(?![#b])[A-Za-z0-9()\-+ ]*(?:\/[A-G](#|b)?(?![#b])[A-Za-z0-9()\-+]*)?$/;
// ST-60: bracket token honoring ChordPro's \[ \] escapes (backslashes unescaped below).
const BRACKET = /\[((?:\\.|[^\[\]\\])*)\]/g;
// Bug report (2026-11-12, "On Facedown there is a [[G]]"): some chord-tab converters emit
// double-bracket chords. BRACKET can only match the INNER [G], so the outer brackets echoed
// as lyric text — a chord wearing visible brackets. The parser reads [[X]] as [X] (lenient
// input); the stored .cho text stays the source of truth, untouched.
// (Hex escapes for the bracket chars — a literal [ inside a class is a useless-escape lint.)
const DOUBLE_BRACKET = /\[\[([^\x5B\x5D]*)\]\]/g;

// ST-41: bare section heading lines ("Verse 1", "Chorus", ...) written as plain text by some
// conversions — modifier words (Final Chorus) plus the section name. Treated as a section marker
// so they render in place as headings instead of lyric lines. ST-48: also accepts a trailing
// colon/period ("Verse1:", "Chorus:") from files that forget the braces. ST-60: multiple
// modifiers ("Second Final Chorus") and numbered variants ("Chorus 2", "Verse 2a", "Bridge 3").
const SECTION_HEADING =
  /^(?:[A-Za-z]+\s+)*(Intro|Outro|Interlude|Pre-?Chorus|Post-?Chorus|Verse\s*\d*[a-z]?|Chorus(\s*\d*[a-z]?)?|Bridge(\s*\d*[a-z]?)?)[.:]?$/i;

const HEADER_NAMES = new Set([
  'title',
  'artist',
  'key',
  'capo',
  'time',
  'tag',
  'genre',
  'year',
  'meta',
]);

// ST-60: mirrors ChordProParser.IndexOutsideQuotes — the separator colon/space of a directive
// counts only OUTSIDE double quotes, so {start_of_verse label="Verse: 1"} no longer splits
// inside the label (which used to name the directive `start_of_verse label="Verse`, match no
// branch, and silently drop the line).
function indexOutsideQuotes(s: string, ch: string): number {
  let inQuotes = false;
  for (let i = 0; i < s.length; i++) {
    if (s[i] === '"') inQuotes = !inQuotes;
    else if (s[i] === ch && !inQuotes) return i;
  }
  return -1;
}

function splitDirective(inner: string): [string, string] {
  const colon = indexOutsideQuotes(inner, ':');
  if (colon > 0) return [inner.slice(0, colon).trim(), inner.slice(colon + 1).trim()];
  const space = indexOutsideQuotes(inner, ' ');
  if (space > 0) return [inner.slice(0, space).trim(), inner.slice(space + 1).trim()];
  return [inner, ''];
}

// ST-60: quote-aware label extraction — with several attributes (label="A" extra="B") the old
// trim-both-quotes produced `A" extra="B`. Quoted labels end at the closing quote; unquoted
// labels end at the first space.
function extractLabel(value: string): string | null {
  const v = value.trim();
  if (!v.startsWith('label=')) return null;
  let rest = v.slice('label='.length).trim();
  if (rest.length >= 2 && rest[0] === '"') {
    const close = rest.indexOf('"', 1);
    rest = close > 0 ? rest.slice(1, close) : rest.slice(1);
  } else {
    const space = rest.indexOf(' ');
    if (space > 0) rest = rest.slice(0, space);
  }
  return rest.length > 0 ? rest : null;
}

// ST-48: short section names ({verse}, {chorus}, {bridge}) are common in the wild — accept them
// alongside the long start_of_* forms. ST-60: the legacy one-letter shorthands ({sov}/{soc}/
// {sob}) appear in older charts and mean the same. Mirrors ChordProParser.TryGetSectionType.
function sectionType(name: string): 'verse' | 'chorus' | 'bridge' | null {
  if (name === 'start_of_verse' || name === 'verse' || name === 'sov') return 'verse';
  if (name === 'start_of_chorus' || name === 'chorus' || name === 'soc') return 'chorus';
  if (name === 'start_of_bridge' || name === 'bridge' || name === 'sob') return 'bridge';
  return null;
}

function isEndName(name: string): boolean {
  // ST-60: the legacy one-letter end shorthands are the counterparts of {sov}/{soc}/{sob} —
  // text after {eoc} used to land inside the chorus.
  return (
    name === 'end_of_verse' ||
    name === 'end_of_chorus' ||
    name === 'end_of_bridge' ||
    name === 'eov' ||
    name === 'eoc' ||
    name === 'eob'
  );
}

// ST-60: shorthand header aliases accepted directly by the parser (mirror of
// ChordProParser.CanonicalizeAlias) — stored charts must not depend on import history.
// Case-sensitive per spec: {c:} = comment, {C:} = capo.
const TEMPO_FORM = /^(\d{1,3}(\s*(bpm|beat|tempo))?\.?|\d+\/\d+)$/i;

function canonicalizeAlias(name: string, value: string): [string, string] {
  switch (name) {
    // A tempo-form {t:...} (120 / 120 bpm / 4/4) is a tempo/meter marking with no header
    // target — dropped, never a title.
    case 't':
      return TEMPO_FORM.test(value) ? ['', value] : ['title', value];
    case 'a':
      return ['artist', value];
    case 'k':
      return ['key', value];
    case 'c':
      return ['comment', value];
    case 'C':
      return ['capo', value];
    default:
      return [name, value];
  }
}

// ST-54: exact-match bar notation tokens. Anything else keeps today's drop behavior.
const BAR_KINDS: Record<string, BarKind> = {
  '|': 'bar',
  '||:': 'repeat-start',
  ':||': 'repeat-end',
};

function parseLine(line: string): ParsedLine {
  const chords: ParsedChord[] = [];
  const annotations: ParsedAnnotation[] = [];
  const bars: ParsedBar[] = [];
  // Bug report (2026-11-12): [[X]] reads as [X] before tokenizing (see DOUBLE_BRACKET).
  line = line.replace(DOUBLE_BRACKET, '[$1]');
  BRACKET.lastIndex = 0;
  let m: RegExpExecArray | null;
  while ((m = BRACKET.exec(line)) !== null) {
    // ST-60: unescape ChordPro's \[ \] inside the token — `sing [C\]m] here` is kept whole.
    const content = m[1].replace(/\\]/g, ']').replace(/\\\[/g, '[');
    if (content.length === 0) continue;
    if (content[0] === '*') {
      annotations.push({ text: content, charIndex: m.index });
    } else if (CHORD_NAME.test(content)) {
      chords.push({ name: content, charIndex: m.index });
    } else {
      const kind = BAR_KINDS[content];
      if (kind) bars.push({ kind, charIndex: m.index });
    }
  }
  return { text: line, chords, annotations, bars };
}

// ST-48: mirrors WebApi/Services/ChordProNormalizer.cs — rewrites common ChordPro variants into
// the canonical form this app parses (shorthand headers {t:/a:/k:/c:}, section aliases like
// {soc} → chorus, bare "Verse1:" headings). The server runs it at import time so stored charts
// are clean; the client runs it for previews so what you see matches what gets saved.

// ST-60: case-sensitive per the ChordPro spec — lowercase {c:} is a comment, uppercase {C:}
// is the capo shorthand (the old case-insensitive map rewrote {C: 3} into {comment: 3}).
const HEADER_SHORTHANDS: Record<string, string> = {
  t: 'title',
  a: 'artist',
  k: 'key',
  c: 'comment',
  C: 'capo',
};
const SECTION_ALIASES: Record<string, string> = {
  verse: 'verse',
  chorus: 'chorus',
  bridge: 'bridge',
  soc: 'chorus',
};
const COMMENT_SECTIONS: Record<string, string> = {
  intro: 'Intro',
  outro: 'Outro',
  interlude: 'Interlude',
  solo: 'Solo',
  refrain: 'Refrain',
  ending: 'Ending',
  coda: 'Coda',
  'pre-chorus': 'Pre-Chorus',
  prechorus: 'Pre-Chorus',
  'post-chorus': 'Post-Chorus',
  postchorus: 'Post-Chorus',
};
// ST-60: widened with the parser's SectionHeading rule — multiple modifiers and numbered
// variants ("Chorus 2", "Verse 2a", "Second Final Chorus"). m[2] carries the number ("Verse 2a").
// The modifier quantifier sits INSIDE the capture group (like the server's (?<mod>(?:…)*?)) —
// JS captures only the last iteration when the quantifier repeats the group itself.
const BARE_HEADING =
  /^((?:[A-Za-z]+\s+)*?)(Intro|Outro|Interlude|Pre-?Chorus|Post-?Chorus|Verse\s*\d*[a-z]?|Chorus(?:\s*\d*[a-z]?)?|Bridge(?:\s*\d*[a-z]?)?)[.:]?$/i;

/** Normalizes common ChordPro variants to canonical form. Idempotent; mirrors the server. */
export function normalizeChordPro(input: string): string {
  return input
    .replace(/\r\n/g, '\n')
    .replace(/\r/g, '\n')
    .split('\n')
    .map((raw) => {
      const line = raw.trim();
      if (line.length === 0) return raw;

      // Whole-line directive: {name} / {name: value} / {name value}.
      if (line.length >= 2 && line[0] === '{' && line[line.length - 1] === '}') {
        const [name, value] = splitDirective(line.slice(1, -1).trim());

        // Shorthand header with a value: {t:Africa} → {title: Africa}, {C: 3} → {capo: 3}
        // (case-sensitive, ST-60). A tempo-form {t:...} (120 / 120 bpm / 4/4) is a tempo/meter
        // marking, not a title — leave as written.
        const lower = name.toLowerCase();
        const shorthand = HEADER_SHORTHANDS[name];
        if (value && shorthand) {
          if (name === 't' && TEMPO_FORM.test(value)) return raw;
          return `{${shorthand}: ${value}}`;
        }

        // Bare section alias: {soc} → {chorus}, {Chorus} → {chorus}, {intro} → {comment: Intro}.
        if (!value) {
          if (SECTION_ALIASES[lower]) return `{${SECTION_ALIASES[lower]}}`;
          if (COMMENT_SECTIONS[lower]) return `{comment: ${COMMENT_SECTIONS[lower]}}`;
        }

        return raw; // unknown directive — leave as written
      }

      // Bare heading line: "Verse1:", "Chorus", "Final Chorus." → typed directive.
      const m = BARE_HEADING.exec(line);
      if (m) {
        const mod = (m[1] ?? '').trim();
        const name = m[2];
        if (/^Verse/i.test(name)) {
          const num = name.match(/\d+/)?.[0] ?? '';
          const base = num ? `Verse ${num}` : 'Verse';
          return mod
            ? `{verse label="${mod} ${base}"}`
            : num
              ? `{verse label="Verse ${num}"}`
              : '{verse}';
        }
        // ST-60: numbered variants carry the number in the label ("Chorus 2" → label "Chorus 2").
        if (/^Chorus/i.test(name)) {
          const label = name.replace(/[.:]+$/, '').trim();
          return /^Chorus$/i.test(label)
            ? mod
              ? `{chorus label="${mod} Chorus"}`
              : '{chorus}'
            : `{chorus label="${mod ? `${mod} ${label}` : label}"}`;
        }
        if (/^Bridge/i.test(name)) {
          const label = name.replace(/[.:]+$/, '').trim();
          return /^Bridge$/i.test(label)
            ? mod
              ? `{bridge label="${mod} Bridge"}`
              : '{bridge}'
            : `{bridge label="${mod ? `${mod} ${label}` : label}"}`;
        }
        // Intro / Outro / Interlude / Pre-Chorus / Post-Chorus → comment heading.
        return `{comment: ${line.replace(/[.:]+$/, '').trim()}}`;
      }

      return raw; // untouched lines keep their original whitespace
    })
    .join('\n');
}

/** ST-60: mirrors ChordProParser.Normalize — joins directives spanning two lines
 * ({comment: X ↵ Y}) so they process instead of rendering literally. Bounded passes. */
function joinMultiLineDirectives(s: string): string {
  const MULTI_LINE = /\{([^{}\n]*)\n([^{}\n]*)\}/g;
  for (let pass = 0; pass < 4; pass++) {
    const joined = s.replace(MULTI_LINE, '{$1 $2}');
    if (joined === s) break;
    s = joined;
  }
  return s;
}

/** Parses raw ChordPro text (normalizing common variants first, ST-48). Never throws. */
export function parseChordPro(input: string): ParsedSongLite {
  const header: ParsedHeader = { meta: [] };
  let hasHeader = false;
  const sections: ParsedSection[] = [];
  const warnings: string[] = [];
  let current: ParsedSection | null = null; // the section currently open (typed or comment)

  const lines = joinMultiLineDirectives(normalizeChordPro(input.replace(/^\uFEFF/, ''))).split(
    '\n',
  );

  for (const raw of lines) {
    const line = raw.trim();
    if (line.length === 0) continue;

    if (line.length >= 2 && line[0] === '{' && line[line.length - 1] === '}') {
      let [name, value] = splitDirective(line.slice(1, -1).trim());
      if (name.length === 0) continue;

      // ST-60: shorthand header aliases ({t:} {a:} {k:} {c:} {C:}) parse directly — stored
      // charts must not depend on import history (the normalizer runs before us, this covers
      // the rest). A tempo-form {t:...} drops (name becomes empty).
      [name, value] = canonicalizeAlias(name, value);
      if (name.length === 0) continue;

      if (HEADER_NAMES.has(name)) {
        hasHeader = true;
        if (name === 'meta') {
          // ST-60: {meta:} / {meta: } names nothing — an empty MetaField is junk.
          if (value.length === 0) continue;
          const space = value.indexOf(' ');
          header.meta.push(
            space > 0
              ? { name: value.slice(0, space), value: value.slice(space + 1).trim() }
              : { name: value, value: '' },
          );
        } else if (value.length > 0) {
          // ST-60: an empty value ({title:}) yields no header field — "" renders as a blank
          // title everywhere; leaving it unset is the app's "not set".
          (header as unknown as Record<string, unknown>)[name] = value;
        }
      } else {
        const type = sectionType(name);
        if (type) {
          current = { type, label: extractLabel(value), lines: [] };
          sections.push(current);
        } else if (isEndName(name)) {
          current = null;
        } else if (name === 'comment') {
          // ST-41: a {comment: ...} line is a section heading of type "other" — it starts a new
          // section and following lyric lines belong to it until the next marker, so comments
          // render in place (e.g. "{comment: Verse 1}").
          current = { type: 'other', label: value || null, lines: [] };
          sections.push(current);
        } else if (name.startsWith('start_of_') || name.startsWith('end_of_')) {
          // ST-60: an unrecognized start_of_*/end_of_* is silent data loss — the marker is
          // skipped (parsing continues) and converted into a visible warning.
          warnings.push(`Unrecognized section directive “${name}” — the marker was skipped.`);
        }
      }
    } else if (SECTION_HEADING.test(line) && !line.includes('[')) {
      // ST-41: a bare heading line starts a new "other" section, like a comment marker.
      // ST-48: strip a trailing colon/period from the label ("Chorus:" → "Chorus").
      current = { type: 'other', label: line.replace(/[.:]+$/, '').trim(), lines: [] };
      sections.push(current);
    } else {
      const parsed = parseLine(line);
      if (current) {
        current.lines.push(parsed);
      } else {
        // Text outside any section (e.g. before the first marker): an unlabeled "other"
        // section that following lines attach to until the next marker.
        current = { type: 'other', label: null, lines: [] };
        sections.push(current);
        current.lines.push(parsed);
      }
    }
  }

  // ST-41: typed sections without an explicit label get one synthesized from their type —
  // verses are numbered in order (Verse 1, Verse 2, ...), choruses and bridges stay plain.
  let verseCount = 0;
  for (const s of sections) {
    if (s.label !== null || s.type === 'other') continue;
    s.label =
      s.type === 'verse' ? `Verse ${++verseCount}` : s.type === 'chorus' ? 'Chorus' : 'Bridge';
  }

  return {
    header: hasHeader ? header : null,
    sections,
    warnings,
  };
}

/** True when the text has at least one lyric line and one chord (the server's IsChordPro rule). */
export function looksLikeChordPro(parsed: ParsedSongLite): boolean {
  let lines = 0;
  let chords = 0;
  for (const s of parsed.sections) {
    for (const l of s.lines) {
      if (l.text.length > 0) lines++;
      chords += l.chords.length;
    }
  }
  return lines > 0 && chords > 0;
}
