import { useEffect, useMemo, useRef, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import {
  apiImport,
  apiMusicBrainzSearch,
  apiPlaylists,
  apiConvertDraft,
  apiConvertSave,
  apiConvertStart,
  apiWishlistUpdate,
} from '../api/client';
import { ApiError } from '../api/types';
import type { Playlist } from '../api/types';
import { useToastStore } from '../stores/toast';
import { useImportQueueStore } from '../stores/importQueue';
import { parseChordPro, looksLikeChordPro } from '../utils/chordpro';
import { matchMusicBrainz, parseFilename } from '../utils/titleGuess';
import { SongRenderer } from '../components/SongRenderer';
import { TextField } from '../components/TextField';
import { SearchTab, wishlistDraftKey } from '../components/SearchTab';
import ui from '../components/ui.module.css';
import styles from './Import.module.css';

import { CONVERSION_PROMPT } from '../prompts/conversionPrompt';

/**
 * Import screen (ST-24): paste ChordPro -> live preview + metadata overrides
 * -> save to library/playlist; plus the chord-conversion helper tab (in-app LLM
 * conversion, ST-14) and the manual copy-to-clipboard prompt path.
 * ST-33: a persistent conversion queue above the tabs — conversions are
 * enqueued without blocking, with live status + streaming preview per draft.
 * ST-44: a Search tab — song lookup (Songsterr + MusicBrainz) and the per-user wishlist.
 * ST-46 bug-57/58: .chopro accepted; multiple files get a confirm panel before import.
 * ST-47 bug-59: validation errors surface their field-level detail (the top-level
 * message is generic — "Invalid request." — while `fields` carries the reason).
 */

// ST-47 bug-59: prefer the server's field-level validation detail over the generic
// top-level message ("Invalid request.") so users see e.g. "Title is required…".
function apiErrorMessage(e: unknown, fallback = 'Import failed.'): string {
  if (e instanceof ApiError && e.fields) {
    const detail = Object.values(e.fields).filter(Boolean).join(' ');
    if (detail) return detail;
  }
  return e instanceof Error ? e.message : fallback;
}

export default function Import() {
  const pushToast = useToastStore((s) => s.push);

  // ST-38 Q2 / ST-44: four tabs — paste, upload (drag & drop), conversion, search + wishlist.
  const [tab, setTab] = useState<'paste' | 'upload' | 'convert' | 'search'>('paste');
  const [text, setText] = useState('');
  const [playlistId, setPlaylistId] = useState('');
  const [playlists, setPlaylists] = useState<Playlist[]>([]);

  // Metadata overrides (blank = use detected header values).
  const [title, setTitle] = useState('');
  const [artist, setArtist] = useState('');

  const [inFlight, setInFlight] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notChordPro, setNotChordPro] = useState(false);

  // ST-37 A4: the draft currently under review in the paste tab. When its exact text is
  // imported we save via /save so the queue row flips to "imported" (safe-to-remove signal).
  const [pendingDraft, setPendingDraft] = useState<{ id: string; text: string } | null>(null);

  // ST-37 B1 / ST-38 Q2: local ChordPro file upload (.cho/.txt) — its own tab, drag & drop.
  // ST-46 bug-58: .chopro is accepted too (it's just ChordPro text).
  const fileRef = useRef<HTMLInputElement>(null);
  const [dragging, setDragging] = useState(false);

  // ST-46 bug-57: multiple files dropped/browsed → a confirm panel listing what will be created.
  type BatchRow = {
    file: File;
    kind: 'text' | 'source' | 'other';
    title?: string;
    artist?: string;
    // ST-50: where the title/artist came from — shown in the panel so guesses are visible.
    titleFrom?: 'header' | 'filename' | 'musicbrainz';
    artistFrom?: 'header' | 'filename' | 'musicbrainz';
    lookingUpArtist?: boolean;
    isChordPro?: boolean;
  };
  const [batchRows, setBatchRows] = useState<BatchRow[] | null>(null);
  const [batchBusy, setBatchBusy] = useState(false);

  // ST-35 G1 / ST-39 A1: raw text OR the preview — one pane at a time at every width.
  const [viewMode, setViewMode] = useState<'raw' | 'preview'>('raw');

  // Convert tab
  const [convertText, setConvertText] = useState('');
  const [copied, setCopied] = useState(false);
  const [convertBusy, setConvertBusy] = useState(false);
  const [convertError, setConvertError] = useState<string | null>(null);

  // ST-33: the conversion queue (store + per-user SignalR events). The initial load and
  // live subscription happen in Shell (ST-35 H1) so the header indicator works everywhere.
  const addDraft = useImportQueueStore((s) => s.addDraft);
  const markSaved = useImportQueueStore((s) => s.markSaved);
  // Bug report (2026-11-12, /import): the "Conversions are running" banner claimed that
  // whenever ANY draft existed — idle converted/imported rows included. It now tracks the
  // rows actually in flight, and disappears when the queue goes quiet.
  const runningCount = useImportQueueStore(
    (s) => s.items.filter((i) => i.status === 'queued' || i.status === 'running').length,
  );

  useEffect(() => {
    apiPlaylists()
      .then(setPlaylists)
      .catch(() => {
        /* no playlist list is non-fatal */
      });
  }, []);

  // Client-side preview (instant).
  const parsed = useMemo(() => {
    if (!text.trim()) return null;
    const p = parseChordPro(text);
    return looksLikeChordPro(p) ? p : null;
  }, [text]);

  const detected = parsed?.header;
  const effTitle = title.trim() || detected?.title || '';
  const effArtist = artist.trim() || detected?.artist || '';

  // ST-46 bug-57 / ST-47: how many of the dropped files will actually be imported.
  // A row needs a title from somewhere — the {title: …} header directive or the filename (ST-50) —
  // which the batch flow sends as an import override; rows with neither are excluded up front.
  const validCount = (batchRows ?? []).filter(
    (r) => r.kind === 'text' && r.isChordPro && !!r.title,
  ).length;

  // ST-50: one short note per row explaining which metadata is a guess, so the user can see it
  // before confirming. MusicBrainz wins over filename (it covers both fields at once).
  function guessNote(r: BatchRow): string | null {
    if (r.lookingUpArtist) return 'looking up artist…';
    const t = r.titleFrom;
    const a = r.artistFrom;
    if (t === 'musicbrainz' || a === 'musicbrainz') return 'from MusicBrainz';
    if (t === 'filename' && a === 'filename') return 'from filename';
    if (t === 'filename') return 'title from filename';
    if (a === 'filename') return 'artist from filename';
    return null;
  }

  // ST-50 follow-up: guesses are editable in the panel — a manual edit clears that field's
  // provenance marker, since it is no longer a guess. Typing a title into an untitled row
  // makes it importable (validCount counts live state).
  function updateRow(
    file: File,
    patch: Partial<Pick<BatchRow, 'title' | 'artist' | 'titleFrom' | 'artistFrom'>>,
  ) {
    setBatchRows((prev) => prev?.map((r) => (r.file === file ? { ...r, ...patch } : r)) ?? null);
  }

  // ST-37 B1: read a local .cho/.txt file straight into the editor (no upload endpoint).
  async function loadFile(file: File | null | undefined) {
    if (!file) return;
    try {
      const content = await file.text();
      setText(content);
      setError(null);
      setNotChordPro(false);
      // ST-38 Q2: review + import happen in the Paste tab.
      setTab('paste');
      pushToast(`Loaded “${file.name}”.`);
    } catch {
      pushToast('Could not read that file.', 'error');
    } finally {
      // Allow re-selecting the same file.
      if (fileRef.current) fileRef.current.value = '';
    }
  }

  // ST-46 bug-58: .chopro is ChordPro text too; pdf/png/jpg attach from the song page later.
  const TEXT_EXTS = new Set(['cho', 'txt', 'chopro']);
  const SOURCE_EXTS = new Set(['pdf', 'png', 'jpg', 'jpeg']);

  function extOf(name: string): string {
    const i = name.lastIndexOf('.');
    return i >= 0 ? name.slice(i + 1).toLowerCase() : '';
  }

  // ST-46 bug-57: one ChordPro text file → the existing editor-review flow; anything else
  // (multiple files, non-text) → a confirm panel listing what will be created.
  async function handleFiles(list: FileList | null) {
    if (!list || list.length === 0) return;
    const files = Array.from(list);
    // Allow re-selecting the same file(s).
    if (fileRef.current) fileRef.current.value = '';

    if (files.length === 1 && TEXT_EXTS.has(extOf(files[0].name))) {
      await loadFile(files[0]);
      return;
    }

    const rows: BatchRow[] = [];
    for (const f of files) {
      const ext = extOf(f.name);
      if (TEXT_EXTS.has(ext)) {
        let title: string | undefined;
        let artist: string | undefined;
        let titleFrom: BatchRow['titleFrom'];
        let artistFrom: BatchRow['artistFrom'];
        let isChordPro = false;
        try {
          const content = await f.text();
          const p = parseChordPro(content);
          if (looksLikeChordPro(p)) {
            isChordPro = true;
            title = p.header?.title;
            artist = p.header?.artist;
            // ST-50: the file's own header wins; the filename only fills what it leaves out.
            if (!title || !artist) {
              const guess = parseFilename(f.name);
              if (!title && guess.title) {
                title = guess.title;
                titleFrom = 'filename';
              }
              if (!artist && guess.artist) {
                artist = guess.artist;
                artistFrom = 'filename';
              }
            }
          }
        } catch {
          /* unreadable — stays flagged below */
        }
        rows.push({ file: f, kind: 'text', title, artist, titleFrom, artistFrom, isChordPro });
      } else if (SOURCE_EXTS.has(ext)) {
        rows.push({ file: f, kind: 'source' });
      } else {
        rows.push({ file: f, kind: 'other' });
      }
    }
    setBatchRows(rows);
    // ST-50: best-effort artist lookup for rows that have a title but no artist yet.
    void resolveArtists(rows);
  }

  // ST-50: fill missing artists via the MusicBrainz search proxy — sequential and spaced
  // (MusicBrainz asks for ≤1 req/s), and failures just leave the artist empty; the row still
  // imports with its title. Rows update in place as results land, so the panel fills in live.
  // Rows are matched by their File reference: row objects are cloned on every state update,
  // but the File itself is stable for the life of the batch.
  async function resolveArtists(rows: BatchRow[]) {
    const need = rows.filter((r) => r.kind === 'text' && r.isChordPro && r.title && !r.artist);
    for (const row of need) {
      setBatchRows(
        (prev) =>
          prev?.map((r) => (r.file === row.file ? { ...r, lookingUpArtist: true } : r)) ?? null,
      );
      try {
        const res = await apiMusicBrainzSearch(row.title!, 5, 'songs');
        const match = matchMusicBrainz(row.title!, res.results);
        if (match) {
          setBatchRows(
            (prev) =>
              prev?.map((r) =>
                // Don't clobber an artist the user typed in while the lookup was in flight.
                r.file === row.file && !r.artist
                  ? {
                      ...r,
                      title: match.title,
                      artist: match.artist,
                      titleFrom: 'musicbrainz',
                      artistFrom: 'musicbrainz',
                    }
                  : r,
              ) ?? null,
          );
        }
      } catch {
        /* best-effort — the row imports with just its title */
      } finally {
        setBatchRows(
          (prev) =>
            prev?.map((r) => (r.file === row.file ? { ...r, lookingUpArtist: false } : r)) ?? null,
        );
      }
      await new Promise((s) => setTimeout(s, 350)); // stay under MusicBrainz's 1 req/s guideline
    }
  }

  // ST-46 bug-57: import every valid ChordPro row sequentially; failures stay in the panel.
  async function createBatch() {
    if (!batchRows || batchBusy) return;
    // Untitled rows are excluded — the server rejects them. ST-50: a title from the filename
    // counts, and the row's resolved title/artist go out as import overrides (body > header).
    const valid = batchRows.filter((r) => r.kind === 'text' && r.isChordPro && !!r.title);
    if (valid.length === 0) return;
    setBatchBusy(true);
    let ok = 0;
    let dups = 0;
    let failed = 0;
    const failedRows = new Set<BatchRow>();
    for (const row of valid) {
      try {
        const content = await row.file.text();
        // ST-50: send the resolved title/artist (header or filename/MusicBrainz guess) — for a
        // file with no {title:} directive this is what makes the import succeed at all.
        const res = await apiImport({
          chordProText: content,
          title: row.title,
          artist: row.artist,
          playlistId: playlistId || undefined,
        });
        // ST-49: duplicates leave the panel too — nothing to retry.
        if (res.duplicate) {
          dups++;
          pushToast(`Skipped “${res.song.title}” — already in your library.`);
        } else {
          ok++;
          pushToast(`Imported “${res.song.title}”.`);
        }
      } catch (e: unknown) {
        failed++;
        failedRows.add(row);
        // ST-47 bug-59: field-level detail (e.g. "Title is required…") beats the generic message.
        pushToast(`${row.file.name}: ${apiErrorMessage(e, 'import failed')}`, 'error');
      }
    }
    setBatchBusy(false);
    // Successful and duplicate rows leave the panel; failures stay so they can be retried.
    setBatchRows((prev) => {
      if (!prev) return null;
      const remaining = prev.filter((r) => failedRows.has(r));
      return remaining.length > 0 ? remaining : null;
    });
    // ST-49: summarize created / skipped-duplicates / failed, omitting zero parts.
    let summary: string;
    if (failed === 0 && dups === 0) {
      summary = `Created ${ok} song${ok === 1 ? '' : 's'}.`;
    } else if (failed === 0 && ok === 0) {
      summary = `All ${dups} already in your library — nothing created.`;
    } else {
      const parts: string[] = [];
      if (ok > 0) parts.push(`created ${ok}`);
      if (dups > 0) parts.push(`skipped ${dups} already in library`);
      if (failed > 0) parts.push(`failed ${failed}`);
      summary = `${parts.join(', ')}.`;
      summary = summary.charAt(0).toUpperCase() + summary.slice(1);
    }
    pushToast(summary, ok + dups === 0 ? 'error' : undefined);
  }

  async function doImport() {
    if (!text.trim()) {
      setError('Paste some ChordPro text first.');
      return;
    }
    setInFlight(true);
    setError(null);
    setNotChordPro(false);
    try {
      // ST-37 A4: importing the reviewed draft's exact text links it via /save, so the
      // queue row flips to "imported". Any edit falls back to a plain import.
      const isReviewedDraft = pendingDraft !== null && text === pendingDraft.text;
      const res = isReviewedDraft
        ? await apiConvertSave(pendingDraft.id, {
            title: title.trim() || undefined,
            artist: artist.trim() || undefined,
            playlistId: playlistId || undefined,
          })
        : await apiImport({
            chordProText: text,
            title: title.trim() || undefined,
            artist: artist.trim() || undefined,
            playlistId: playlistId || undefined,
          });
      if (isReviewedDraft) {
        markSaved(pendingDraft.id, res.song.id);
        // ST-44: if this draft came from a wishlist item's paste box, close the loop.
        const wlId = localStorage.getItem(wishlistDraftKey(pendingDraft.id));
        if (wlId) {
          localStorage.removeItem(wishlistDraftKey(pendingDraft.id));
          apiWishlistUpdate(wlId, { status: 'converted', convertedSongId: res.song.id }).catch(
            () => {},
          );
        }
        setPendingDraft(null);
      }
      // ST-39 A2: no success screen — a toast is enough; clear the form for the next song.
      // ST-49: re-importing an existing chart says so instead of pretending it was created.
      pushToast(
        res.duplicate
          ? `“${res.song.title}” is already in your library — no duplicate created.`
          : `Imported “${res.song.title}”${res.song.artist ? ` by ${res.song.artist}` : ''}.`,
      );
      setText('');
      setTitle('');
      setArtist('');
      setPlaylistId('');
    } catch (e: unknown) {
      if (e instanceof ApiError && e.code === 'not-chordpro') {
        setNotChordPro(true);
        setError('This doesn\'t look like ChordPro — see the "Convert" tab for help.');
      } else {
        // ST-47 bug-59: field-level detail (e.g. "Title is required…") beats the generic message.
        setError(apiErrorMessage(e));
      }
    } finally {
      setInFlight(false);
    }
  }

  async function copyPromptAndContent() {
    const payload = `${CONVERSION_PROMPT}\n\n=== INPUT ===\n${convertText.trim()}`;
    try {
      await navigator.clipboard.writeText(payload);
      setCopied(true);
      window.setTimeout(() => setCopied(false), 2500);
    } catch {
      setError('Could not access the clipboard. Copy manually from the prompt box below.');
    }
  }

  // ST-33: in-app LLM conversion. Enqueues a server-side job and returns immediately —
  // progress (and the live preview) shows up in the queue above, so you can keep pasting
  // songs back-to-back. Nothing is saved until a draft is reviewed + imported.
  async function convertInApp() {
    const content = convertText.trim();
    if (!content || convertBusy) return;
    setConvertBusy(true);
    setConvertError(null);
    try {
      const { id } = await apiConvertStart({ content });
      addDraft({
        id,
        status: 'queued',
        inputPreview: firstLine(content),
        createdAt: new Date().toISOString(),
      });
      setConvertText('');
      pushToast('Added to the conversion queue — keep pasting songs.');
    } catch (e: unknown) {
      if (e instanceof ApiError && (e.code === 'llm-not-configured' || e.status === 503)) {
        setConvertError(
          'In-app conversion is not configured yet — set up an LLM in Settings → LLM conversion. ' +
            'Or use the manual steps below: copy the prompt + content into any LLM chat.',
        );
      } else {
        setConvertError(e instanceof Error ? e.message : 'Could not start the conversion.');
      }
    } finally {
      setConvertBusy(false);
    }
  }

  // ST-33: load a succeeded draft's ChordPro into the paste tab for review (nothing saved yet).
  async function reviewDraft(id: string) {
    try {
      const draft = await apiConvertDraft(id);
      if (draft.chordProText) {
        setText(draft.chordProText);
        // ST-37 A4: remember this draft so importing its text links it via /save.
        setPendingDraft({ id, text: draft.chordProText });
        setTab('paste');
        pushToast('Converted — review it in the Paste ChordPro tab, then import.');
      } else {
        pushToast('This draft has no result to review.', 'error');
      }
    } catch (e: unknown) {
      pushToast(e instanceof Error ? e.message : 'Could not load the draft.', 'error');
    }
  }

  // Bug report #103: Review on the queue page lands here — /import?review=<id>
  // loads that draft into the paste tab, then the param is cleaned from the URL.
  const [reviewParams, setReviewParams] = useSearchParams();
  useEffect(() => {
    const rid = reviewParams.get('review');
    if (!rid) return;
    void reviewDraft(rid);
    const next = new URLSearchParams(reviewParams);
    next.delete('review');
    setReviewParams(next, { replace: true });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [reviewParams]);

  function firstLine(t: string): string | null {
    const s = t.trim();
    if (!s) return null;
    const nl = s.indexOf('\n');
    let line = (nl > 0 ? s.slice(0, nl) : s).trim();
    if (line.length > 80) line = `${line.slice(0, 80)}…`;
    return line || null;
  }

  return (
    <div className={styles.page}>
      {/* Bug report #103: the conversion queue lives on its own page now (nav "Queue") —
          it no longer sits permanently at the right of the import form. */}
      {runningCount > 0 && (
        <div className={styles.queueBar}>
          <span className={ui.muted}>
            Conversions are running in the background ({runningCount} in progress).
          </span>
          <Link to="/queue" className={`${ui.btn} ${ui.small}`}>
            Open queue →
          </Link>
        </div>
      )}
      <div className={styles.layout}>
        <div className={styles.mainCol}>
          <div className={styles.tabs} role="tablist" aria-label="Import methods">
            <button
              type="button"
              role="tab"
              aria-selected={tab === 'paste'}
              className={`${styles.tab} ${tab === 'paste' ? styles.tabActive : ''}`}
              onClick={() => setTab('paste')}
            >
              Paste ChordPro
            </button>
            {/* ST-38 Q2: file upload is its own tab (drag & drop + browse). */}
            <button
              type="button"
              role="tab"
              aria-selected={tab === 'upload'}
              className={`${styles.tab} ${tab === 'upload' ? styles.tabActive : ''}`}
              onClick={() => setTab('upload')}
            >
              Upload file
            </button>
            <button
              type="button"
              role="tab"
              aria-selected={tab === 'convert'}
              className={`${styles.tab} ${tab === 'convert' ? styles.tabActive : ''}`}
              onClick={() => setTab('convert')}
            >
              Convert
            </button>
            {/* ST-44: song search + per-user wishlist. */}
            <button
              type="button"
              role="tab"
              aria-selected={tab === 'search'}
              className={`${styles.tab} ${tab === 'search' ? styles.tabActive : ''}`}
              onClick={() => setTab('search')}
            >
              Search
            </button>
          </div>

          {tab === 'paste' ? (
            <>
              {/* ST-35 G1 / ST-39 A1: raw/preview toggle — one pane at a time, every width. */}
              <div
                className={styles.viewToggle}
                role="tablist"
                aria-label="Show raw text or preview"
              >
                <button
                  type="button"
                  role="tab"
                  aria-selected={viewMode === 'raw'}
                  className={`${styles.viewToggleBtn} ${viewMode === 'raw' ? styles.viewToggleActive : ''}`}
                  onClick={() => setViewMode('raw')}
                >
                  Raw
                </button>
                <button
                  type="button"
                  role="tab"
                  aria-selected={viewMode === 'preview'}
                  className={`${styles.viewToggleBtn} ${viewMode === 'preview' ? styles.viewToggleActive : ''}`}
                  onClick={() => setViewMode('preview')}
                >
                  Preview
                </button>
              </div>

              <div
                className={`${styles.pasteGrid} ${viewMode === 'preview' ? styles.showPreview : ''}`}
              >
                <div className={styles.pasteCol}>
                  {/* ST-39 A1: the Import CTA lives at the top of the form, not after a scroll. */}
                  <div className={styles.editorHead}>
                    <label className={styles.label} htmlFor="import-text">
                      ChordPro text
                    </label>
                    <button
                      type="button"
                      className={`${ui.btn} ${ui.primary}`}
                      onClick={() => void doImport()}
                      disabled={inFlight || !text.trim()}
                    >
                      {inFlight ? 'Importing…' : 'Import'}
                    </button>
                  </div>
                  <textarea
                    id="import-text"
                    className={styles.editor}
                    value={text}
                    onChange={(e) => {
                      setText(e.target.value);
                      setError(null);
                      setNotChordPro(false);
                    }}
                    placeholder={
                      '{title: My Song}\n{artist: The Band}\n{key: G}\n\n[G] Type your [D] chords [Em] inline, [C] before the words'
                    }
                  />

                  <div className={styles.metaPanel}>
                    <h2 className={styles.panelTitle}>Metadata</h2>
                    <p className={ui.muted}>
                      Blank fields fall back to the {`{title:}`}/{`{artist:}`} header directives
                      (detected: {effTitle || 'none'}
                      {effArtist ? ` / ${effArtist}` : ''}).
                    </p>
                    <div className={styles.metaGrid}>
                      <TextField
                        label="Title (override)"
                        value={title}
                        onChange={setTitle}
                        placeholder={detected?.title ?? 'From header…'}
                      />
                      <TextField
                        label="Artist (override)"
                        value={artist}
                        onChange={setArtist}
                        placeholder={detected?.artist ?? 'From header…'}
                      />
                      <label className={styles.field}>
                        <span className={styles.label}>Target playlist (optional)</span>
                        <select
                          className={styles.select}
                          value={playlistId}
                          onChange={(e) => setPlaylistId(e.target.value)}
                        >
                          <option value="">— none —</option>
                          {playlists.map((p) => (
                            <option key={p.id} value={p.id}>
                              {p.name}
                            </option>
                          ))}
                        </select>
                      </label>
                    </div>
                  </div>

                  {error && (
                    <div className={ui.errorBanner} role="alert">
                      <span>{error}</span>
                      {notChordPro && (
                        <button
                          type="button"
                          className={`${ui.btn} ${ui.small}`}
                          onClick={() => setTab('convert')}
                        >
                          Open helper tab
                        </button>
                      )}
                    </div>
                  )}
                </div>

                <aside className={styles.previewCol} aria-label="Preview">
                  <h2 className={styles.panelTitle}>Preview</h2>
                  {parsed ? (
                    <SongRenderer parsed={parsed} />
                  ) : (
                    <p className={ui.muted}>
                      No chord content detected yet. Paste text with at least one [chord] token.
                    </p>
                  )}
                </aside>
              </div>
            </>
          ) : tab === 'upload' ? (
            <div className={styles.uploadCol}>
              {/* ST-46 bug-57: multi-file confirm panel — shows what will be created before importing. */}
              {batchRows && (
                <div className={styles.batchPanel} role="dialog" aria-label="Confirm import">
                  <h2 className={styles.panelTitle}>
                    Create {validCount} song{validCount === 1 ? '' : 's'}?
                  </h2>
                  <ul className={styles.batchList}>
                    {batchRows.map((r, i) => {
                      const note =
                        r.kind === 'text' && r.isChordPro && r.title ? guessNote(r) : null;
                      return (
                        <li
                          // ST-64: index-prefixed — the old name+size key collided when the
                          // same file was picked twice (duplicate React keys in one list).
                          // Rows are built once and only mapped over, never spliced, so the
                          // index is stable for the dialog's lifetime.
                          key={`${i}-${r.file.name}-${r.file.size}`}
                          className={`${styles.batchRow} ${
                            r.kind !== 'text' || !r.isChordPro || !r.title
                              ? styles.batchRowMuted
                              : ''
                          }`}
                        >
                          <span className={styles.batchName}>{r.file.name}</span>
                          {/* ST-50 follow-up: title/artist are editable before import — fix a wrong
                          MusicBrainz guess, or type a title to rescue an untitled row. */}
                          {r.kind === 'text' && r.isChordPro && (
                            <span className={styles.batchMeta}>
                              <input
                                className={styles.batchInput}
                                value={r.title ?? ''}
                                placeholder="Title"
                                disabled={batchBusy}
                                onChange={(e) =>
                                  updateRow(r.file, {
                                    title: e.target.value.trim() || undefined,
                                    titleFrom: undefined,
                                  })
                                }
                              />
                              <span aria-hidden>—</span>
                              <input
                                className={styles.batchInput}
                                value={r.artist ?? ''}
                                placeholder="Artist"
                                disabled={batchBusy}
                                onChange={(e) =>
                                  updateRow(r.file, {
                                    artist: e.target.value.trim() || undefined,
                                    artistFrom: undefined,
                                  })
                                }
                              />
                              {note ? <span className={ui.muted}>· {note}</span> : null}
                            </span>
                          )}
                          {r.kind === 'text' && !r.isChordPro && (
                            <span className={styles.batchFlag}>doesn’t look like ChordPro</span>
                          )}
                          {r.kind === 'source' && (
                            <span className={styles.batchFlag}>
                              attach from the song page after import
                            </span>
                          )}
                          {r.kind === 'other' && (
                            <span className={styles.batchFlag}>skipped — unsupported type</span>
                          )}
                        </li>
                      );
                    })}
                  </ul>
                  <div className={styles.batchActions}>
                    <button
                      type="button"
                      className={`${ui.btn} ${ui.primary}`}
                      onClick={() => void createBatch()}
                      disabled={batchBusy || validCount === 0}
                    >
                      {batchBusy
                        ? 'Importing…'
                        : `Create ${validCount} song${validCount === 1 ? '' : 's'}`}
                    </button>
                    <button
                      type="button"
                      className={ui.btn}
                      onClick={() => setBatchRows(null)}
                      disabled={batchBusy}
                    >
                      Cancel
                    </button>
                  </div>
                </div>
              )}

              {/* ST-38 Q2 / ST-46: drag & drop zone — one ChordPro file loads into the editor;
              several files (or pdf/png/jpg) get a confirm panel first. */}
              {!batchRows && (
                <div
                  className={`${styles.uploadZone} ${dragging ? styles.uploadZoneDragging : ''}`}
                  onDragOver={(e) => {
                    e.preventDefault();
                    setDragging(true);
                  }}
                  onDragLeave={(e) => {
                    if (!e.currentTarget.contains(e.relatedTarget as Node)) setDragging(false);
                  }}
                  onDrop={(e) => {
                    e.preventDefault();
                    setDragging(false);
                    void handleFiles(e.dataTransfer.files);
                  }}
                >
                  <input
                    ref={fileRef}
                    type="file"
                    accept=".cho,.txt,.chopro,text/plain"
                    multiple
                    hidden
                    onChange={(e) => void handleFiles(e.target.files)}
                  />
                  <p>Drag &amp; drop ChordPro files here</p>
                  <p className={ui.muted}>
                    .cho, .txt or .chopro — one file loads into the editor for review; several get a
                    confirm screen first. pdf/png/jpg attach from the song page after import.
                  </p>
                  <button type="button" className={ui.btn} onClick={() => fileRef.current?.click()}>
                    Choose files…
                  </button>
                </div>
              )}
            </div>
          ) : tab === 'search' ? (
            <SearchTab />
          ) : (
            <div className={styles.convertCol}>
              <div className={ui.card}>
                <h2>Paste chord text from anywhere</h2>
                <p className={ui.muted}>
                  Chord pages aren’t scraped automatically (ToS/anti-bot) — copy the page text from
                  your browser (Ctrl+A, Ctrl+C), paste it below, and convert it with this server’s
                  LLM. No LLM configured? Use the manual steps at the bottom.
                </p>

                <label className={styles.label} htmlFor="convert-text">
                  Chord page text
                </label>
                <textarea
                  id="convert-text"
                  className={styles.editorSmall}
                  value={convertText}
                  onChange={(e) => setConvertText(e.target.value)}
                  placeholder="Paste the chord page text here (lyrics + chord lines)…"
                />

                <button
                  type="button"
                  className={`${ui.btn} ${ui.primary}`}
                  onClick={() => void convertInApp()}
                  disabled={convertText.trim() === '' || convertBusy}
                >
                  {convertBusy ? 'Adding…' : 'Add to conversion queue'}
                </button>
                <p className={ui.muted}>
                  Runs as a background job on this server’s LLM, one song at a time. Watch it in the
                  conversion queue above (live preview included) and keep pasting more songs —
                  nothing is saved until you review + import.
                </p>
                {convertError && (
                  <p className={ui.errorBanner} role="alert">
                    {convertError}
                  </p>
                )}

                <hr className={styles.hr} />

                <h3>No LLM? Convert manually</h3>
                <ol className={styles.steps}>
                  <li>Paste the chord page text (lyrics + chords) into the box above.</li>
                  <li>
                    Click “Copy prompt + content” — it puts the conversion prompt and your text on
                    the clipboard.
                  </li>
                  <li>
                    Paste that into any LLM chat (e.g. Qwen3.8 via Ollama WebUI or LM Studio).
                  </li>
                  <li>Copy the ChordPro result into the “Paste ChordPro” tab and import.</li>
                </ol>
                <button
                  type="button"
                  className={ui.btn}
                  onClick={() => void copyPromptAndContent()}
                  disabled={!convertText.trim()}
                >
                  {copied ? 'Copied ✓' : 'Copy prompt + content'}
                </button>

                <details className={styles.promptDetails}>
                  <summary>View the conversion prompt</summary>
                  <pre className={styles.promptBox}>{CONVERSION_PROMPT}</pre>
                </details>
              </div>

              <p className={ui.muted}>
                Tip: the full conversion prompt also lives in the repo at{' '}
                <code>docs/prompts/chord-page-to-chordpro.md</code>.
              </p>
              <p>
                <Link to="/" className={ui.muted}>
                  ← Back to the dashboard
                </Link>
              </p>
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
