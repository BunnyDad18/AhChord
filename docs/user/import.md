# Import

The **Import** page has four tabs — pick by what you have in hand.

## 1 · Paste ChordPro

Paste chart text written in ChordPro. A **live preview** renders it as it will
play, parsed metadata (title / artist / key / capo) is guessed and **editable**,
and you can drop the song straight into a target playlist.

If the text isn't ChordPro, the app says so plainly and points at the Convert tab.

## 2 · Upload file

Drag & drop (or browse) `.cho` / `.txt` files — a batch at a time. Each file goes
through the same preview → review → import flow as the Paste tab.

## 3 · Convert (chord page → ChordPro, in-app)

For the pages that aren't ChordPro — a chord page from any site.

1. Paste the page's text (copy it from your browser: Ctrl+A, Ctrl+C).
2. **Convert in app** queues a background job; the ChordPro streams in live while
   the model works (leave the page and the job keeps running).
3. Progress, retries and history live on the **Queue** page (`/queue`).
4. **Review** loads the converted chart into the Paste tab — **nothing is saved
   until you import it**, so you can fix chords, sections, metadata first.

Notes:

- The conversion runs against a **local LLM you configure** (Ollama / LM Studio /
  any OpenAI-compatible endpoint) — see [Settings → LLM conversion](settings.md#llm-conversion).
  With no model configured the tab reports *Not configured* and the rest of
  Import is unaffected.
- **No server LLM? Use the manual path:** the tab embeds the full conversion
  prompt — **Copy prompt + content**, paste both into any chat LLM
  (ChatGPT, a local model, …), paste the answer into the Paste tab. The same
  prompt ships at [`docs/prompts/chord-page-to-chordpro.md`](../prompts/chord-page-to-chordpro.md).
- Converted charts are a best effort: **review the chords before rehearsal.**
  The prompt keeps section headings as `{comment: …}` lines (Verse 1, Chorus, …)
  and refuses invented sections, but a model can still harmonise differently than
  you'd like.
- Import is deliberately **paste-based** — the app fetches nothing. You pasting a page you
  have access to is between you and the site's terms; the app just converts text.

## 4 · Search (find what's missing)

Song lookup across **Songsterr + MusicBrainz** for songs you don't have yet.
Anything you find that the library lacks goes on your **wishlist** — your
per-user "songs to get" list; kicking off a conversion from a wishlist item
links the two, and the item clears when the song lands in the library.

## Where did my metadata come from?

Imported/converted charts carry whatever metadata the text exposes
(title/artist/key/capo directives). The editor's **MusicBrainz lookup** fills the
rest (artist / album / year), and the library-wide [metadata review](library-and-songs.md#metadata-review)
applies server-proposed fixes in batches once you accept them.
