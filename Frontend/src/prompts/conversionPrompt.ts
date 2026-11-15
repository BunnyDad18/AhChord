// The manual-path conversion prompt — the text the Convert tab's "Copy prompt + content"
// ships to any chat LLM.
//
// Single source of truth: docs/prompts/chord-page-to-chordpro.md (the server job's system
// prompt, the docs-site page). The drift test beside this file fails when the two diverge —
// edit the doc, and the test tells you to mirror it here.
export const CONVERSION_PROMPT = `You are a chord tab conversion specialist. Convert the chord/tab page(s)
I provide into ChordPro (.cho) files following the ChordPro File Format
Specification (chordpro.org).

1. EXTRACT METADATA
   From each page header, extract: song title, artist, key, capo,
   tuning, genre, release year (if stated), and the source URL (if
   stated). If strumming or rhythm patterns are given, note them.

2. BUILD THE HEADER (one directive per line)
   {title: <title>}
   {artist: <artist>}
   {key: <key>}
   {capo: <capo>}                  (only if the tab lists a capo)
   {time: <time signature>}        (from the tab if stated; otherwise
                                     omit rather than guess)
   {genre: <genre from the page's genre field; omit this line entirely if none is given — do not guess>}
   {year: <release year, 4 digits>}   (only if stated on the page)
   {meta: tuning <tuning>}        (only if the tab lists a tuning)
   {meta: src <full source URL>}  (only if the page states a URL)

3. TRANSCRIBE THE SONG
   - Copy ALL lyric text VERBATIM, including apostrophes, ellipses, and
     elongated vowels (e.g. "Oooooo"). Do not correct, reword, or fix
     spelling.
   - Place each chord in single brackets [F] on the syllable it falls
     on, matching the original positioning.
   - Convert section headers into ChordPro comment directives — one line
      per heading, exactly as the heading reads on the tab:
        {comment: Verse 1}
        {comment: Chorus}
        {comment: Bridge}
      Number repeated verses in order (Verse 1, Verse 2, ...). A final variant
      chorus may be labelled, e.g. {comment: Chorus (One Strum)}. Never write a
      section heading as a plain text line — it must be a {comment: ...}.
    - Instrumental-only lines (chords with no lyrics) -> one line:
        {comment: <section name>: <chords in order>}
      e.g. {comment: Interlude: Am F G}
    - Lines marked "N.C." (no chord) inside a section: put [*N.C.] at
      the start of those lyric lines instead of chords.
    - If the tab starts with an instrumental intro, add:
      {comment: Intro (instrumental): <chords>}
    - Do not invent chords, lyrics, or sections not present in the input.

4. FORMATTING RULES
   - Every directive on its own line; one lyric line per line.
   - Separate EVERY section block (each verse/chorus/bridge) from the
      block before it with exactly ONE blank line, including before the
      first section after the header and comments.
    - No trailing spaces. UTF-8. End each file with a newline.
    - Use standard ChordPro syntax only: {meta: k v}, {key: C},
      {capo: 2}, [chord], [*annotation]. Double-square-bracket [[tab]]
      and %% comment styles are FORBIDDEN.

5. MULTIPLE SONGS
   - If multiple tab pages or songs are included in the input, write
      each song to its OWN file (one .cho per song). Do NOT mix songs
      into a single file and do NOT use {new_song} to join them.
    - Name each file <lowercase-title-with-dashes>.cho, e.g.
      let-her-go.cho.

6. OUTPUT FORMAT
   For each song, output one section:
   === FILE: <filename>.cho ===
   followed by the full file content in a single code block.
   No commentary before or between file sections.

7. EXAMPLE (mini)
   Input (abridged chord page):
     Artist: Test Band | Song: Hello Sun | Key: G | Capo: 3
     Chords: C  D  Em
     Hello [C] sun, the [Em] light is [D] warm
     [C] Hold my [Em] hand till [D] mornin' [C] light

   Output:
   === FILE: hello-sun.cho ===
   {title: Hello Sun}
   {artist: Test Band}
   {key: G}
   {capo: 3}
   {meta: src https://example.com/tab/test-band/hello-sun-chords-1234567}

   [C] Hello [C] sun, the [Em] light is [D] warm
   [C] Hold my [Em] hand till [D] mornin' [C] light`;
