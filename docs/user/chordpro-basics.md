# ChordPro basics

AhChord stores charts as [ChordPro](https://www.chordpro.org/) — plain text with
chords in `[square brackets]`, inline before the syllable they belong to. You need
this page for exactly two things: importing a clean chart, and touching up a
converted one.

## A chart at a glance

```
{title: Let It Be}
{artist: Lennon–McCartney}
{key: C}

{start_of_verse}
[ C ]Let it be and there will be an [G]answer, my [Am]friend
[ F ]Let it be, take a [C]long and lonely [G]road
{end_of_verse}

{start_of_chorus}
[ C ]Let it be, [Am]let it be, [F]let it be, [G]let it be
{end_of_chorus}
```

- **Directives** in braces at the top: `title`, `artist`, `key`, `capo`, `time`
  (and `{meta: tuning drop D}` for tunings). Import reads these — they fill the
  metadata fields; the editor's MusicBrainz lookup fills in album/year.
- **Chords** sit in `[brackets]` immediately **before the syllable** that changes:
  `Pre[Am]cious memories`. Alignment in the text file is cosmetic — what matters
  is the order.
- **Sections** wrap the block: `{start_of_verse}`/`{end_of_verse}`,
  `{start_of_chorus}`/`{end_of_chorus}`, `{start_of_bridge}`/`{end_of_bridge}`.
  Sections are what give the player its labels and structure — a chart without
  them renders, just flatter.
- `{comment: …}` is a note to humans, not rendered as lyrics.

## What AhChord does with it

- The **text is the truth**: chords, sections, lyrics all render from it
  (parsed tables are a cache, never the source).
- The header directives set the song's **key** and stored **capo**; live
  **transposition** is display-only and never writes to this text.
- Chord names feed the **diagram tooltips** (guitar/piano) automatically — no
  diagram data to maintain.

## If your chart isn't ChordPro

Paste it into **Import → Convert** (in-app, local LLM) or use the bundled
prompt with any chat LLM —
[`docs/prompts/chord-page-to-chordpro.md`](../prompts/chord-page-to-chordpro.md) is the full
conversion prompt and doubles as a checklist of the conventions above.

For the full specification (tab blocks, font directives, multiple voices):
[chordpro.org](https://www.chordpro.org/chordpro/chordpro-file-format-specification/).
AhChord is deliberately permissive — most charts that follow the basics above
render exactly as intended.
