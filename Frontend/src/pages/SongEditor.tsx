import { useEffect, useMemo, useState } from 'react';
import { useBlocker, useNavigate, useParams } from 'react-router-dom';
import {
  apiDetectKey,
  apiMusicBrainzSearch,
  apiSong,
  apiSongCreate,
  apiSongDelete,
  apiSongUpdate,
} from '../api/client';
import { ApiError } from '../api/types';
import type { MusicBrainzResult } from '../api/types';
import { useToastStore } from '../stores/toast';
import { parseChordPro, looksLikeChordPro } from '../utils/chordpro';
import { SongRenderer } from '../components/SongRenderer';
import { TagInput } from '../components/TagInput';
import { TextField } from '../components/TextField';
import { TrashIcon } from '../components/Icons';
import ui from '../components/ui.module.css';
import styles from './SongEditor.module.css';

const KEYS = [
  'C', 'C#', 'D', 'D#', 'E', 'F', 'F#', 'G', 'G#', 'A', 'A#', 'B',
  'Cm', 'C#m', 'Dm', 'D#m', 'Em', 'Fm', 'F#m', 'Gm', 'G#m', 'Am', 'A#m', 'Bm',
];

interface FormState {
  title: string;
  // ST-106: multi-valued artists/genres + free tags (the tag link tables are the truth).
  artists: string[];
  album: string;
  year: string;
  key: string;
  genres: string[];
  tags: string[];
  // ST-52: tempo (BPM).
  tempo: string;
  capoFret: number;
  sourceUrl: string;
  chordProText: string;
}

const EMPTY: FormState = {
  title: '',
  artists: [],
  album: '',
  year: '',
  key: '',
  genres: [],
  tags: [],
  tempo: '',
  capoFret: 0,
  sourceUrl: '',
  chordProText: '',
};

type FieldErrors = Partial<Record<keyof FormState, string>>;

interface EditorProps {
  mode: 'new' | 'edit';
}

/**
 * Song editor (ST-22): metadata + ChordPro text with a live client-side
 * preview, MusicBrainz lookup assist, save (with unsaved-changes guard) and
 * typed-confirmation delete.
 */
export default function SongEditor({ mode }: EditorProps) {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const pushToast = useToastStore((s) => s.push);

  const [form, setForm] = useState<FormState>(EMPTY);
  const [initial, setInitial] = useState<string>(JSON.stringify(EMPTY));
  const [loading, setLoading] = useState(mode === 'edit');
  const [fieldErrors, setFieldErrors] = useState<FieldErrors>({});
  const [formError, setFormError] = useState<string | null>(null);
  const [inFlight, setInFlight] = useState(false);
  const [showPreview, setShowPreview] = useState(true);

  // ST-106: key-derivation state from the loaded song (drives the hint under the Key field).
  const [keyMeta, setKeyMeta] = useState<{ override: boolean; confidence: number | null }>({
    override: false,
    confidence: null,
  });
  const [detecting, setDetecting] = useState(false);

  // MusicBrainz assist.
  const [mbLoading, setMbLoading] = useState(false);
  const [mbError, setMbError] = useState<string | null>(null);
  const [mbResults, setMbResults] = useState<MusicBrainzResult[] | null>(null);

  // Delete confirmation (ST-35 D3: plain confirm/cancel — no typed title).
  const [confirmDelete, setConfirmDelete] = useState(false);

  const songId = mode === 'edit' ? id : undefined;

  useEffect(() => {
    if (mode !== 'edit' || !id) return;
    apiSong(id)
      .then(({ song }) => {
        // ST-106: the tag lists are the truth; fall back to the legacy flat cache for
        // rows the backfill has not touched (offline caches of pre-ST-106 responses).
        const next: FormState = {
          title: song.title,
          artists: song.artists?.length ? song.artists : song.artist ? [song.artist] : [],
          album: song.album ?? '',
          year: song.year ? String(song.year) : '',
          key: song.key ?? '',
          genres: song.genres?.length ? song.genres : song.genre ? [song.genre] : [],
          tags: song.tags ?? [],
          tempo: song.tempo ? String(song.tempo) : '',
          capoFret: song.capoFret,
          sourceUrl: song.sourceUrl ?? '',
          chordProText: song.chordProText,
        };
        setForm(next);
        setInitial(JSON.stringify(next));
        setKeyMeta({ override: song.keyOverride ?? false, confidence: song.keyConfidence ?? null });
      })
      .catch((e: unknown) =>
        setFormError(e instanceof Error ? e.message : 'Could not load the song.'),
      )
      .finally(() => setLoading(false));
  }, [mode, id]);

  const dirty = useMemo(() => JSON.stringify(form) !== initial, [form, initial]);

  // ST-106: the key as loaded/saved last — the hint says when the field diverges from it.
  const initialKey = useMemo(() => {
    try {
      const parsed: unknown = JSON.parse(initial);
      return typeof parsed === 'object' && parsed !== null && 'key' in parsed
        ? String((parsed as { key: unknown }).key ?? '')
        : '';
    } catch {
      return '';
    }
  }, [initial]);

  // Unsaved-changes guard on navigation (ST-22).
  const blocker = useBlocker(dirty && !inFlight);

  const set = (patch: Partial<FormState>) => {
    setForm((f) => ({ ...f, ...patch }));
    setFieldErrors((e) => {
      const next = { ...e };
      for (const k of Object.keys(patch)) delete next[k as keyof FormState];
      return next;
    });
  };

  function onTab(e: React.KeyboardEvent<HTMLTextAreaElement>) {
    if (e.key === 'Tab') {
      e.preventDefault();
      const el = e.currentTarget;
      const start = el.selectionStart;
      const end = el.selectionEnd;
      const next = form.chordProText.slice(0, start) + '  ' + form.chordProText.slice(end);
      set({ chordProText: next });
      requestAnimationFrame(() => {
        el.selectionStart = el.selectionEnd = start + 2;
      });
    }
  }

  const lineCount = form.chordProText.length === 0 ? 0 : form.chordProText.split('\n').length;

  // Client-side live preview (instant, no server round-trip).
  const preview = useMemo(() => {
    if (!form.chordProText.trim()) return null;
    const parsed = parseChordPro(form.chordProText);
    return looksLikeChordPro(parsed) ? parsed : null;
  }, [form.chordProText]);

  async function lookupMusicBrainz() {
    const q = [form.artists[0], form.title].filter((s) => s?.trim()).join(' ');
    if (q.trim().length < 2) {
      setMbError('Enter a title and/or artist first.');
      return;
    }
    setMbLoading(true);
    setMbError(null);
    setMbResults(null);
    try {
      const res = await apiMusicBrainzSearch(q, 8);
      setMbResults(res.results);
    } catch (e: unknown) {
      setMbError(
        e instanceof ApiError && e.status === 503
          ? 'MusicBrainz is rate-limiting — try again in a second.'
          : e instanceof Error
            ? e.message
            : 'MusicBrainz lookup failed.',
      );
    } finally {
      setMbLoading(false);
    }
  }

  function applyMb(r: MusicBrainzResult) {
    set({
      // ST-106: MusicBrainz joins multi-artist credits with commas — split into chips.
      artists: r.artist
        ? r.artist.split(',').map((a) => a.trim()).filter(Boolean)
        : form.artists,
      album: r.album ?? form.album,
      year: r.year ? String(r.year) : form.year,
    });
    setMbResults(null);
  }

  function validate(): boolean {
    const errs: FieldErrors = {};
    if (!form.title.trim()) errs.title = 'Title is required.';
    if (!form.chordProText.trim()) errs.chordProText = 'ChordPro text is required.';
    if (form.year) {
      const y = Number(form.year);
      if (!Number.isInteger(y) || y < 1800 || y > 2100) errs.year = 'Year must be 1800–2100.';
    }
    // ST-52: same range as the API (MetadataReviewJob.NormalizeValue).
    if (form.tempo) {
      const t = Number(form.tempo);
      if (!Number.isInteger(t) || t < 30 || t > 300) errs.tempo = 'Tempo must be 30–300 BPM.';
    }
    setFieldErrors(errs);
    return Object.keys(errs).length === 0;
  }

  async function save() {
    if (!validate()) return;
    setFormError(null);
    setInFlight(true);
    const payload = {
      title: form.title.trim(),
      album: form.album.trim() || undefined,
      year: form.year ? Number(form.year) : undefined,
      // ST-106: Key is always sent — a value is a hand-set override (survives
      // re-saves), "" clears it and hands the key back to the chord analyzer.
      key: form.key.trim(),
      tempo: form.tempo ? Number(form.tempo) : undefined,
      capoFret: form.capoFret,
      sourceUrl: form.sourceUrl.trim() || undefined,
      chordProText: form.chordProText,
      // ST-106: the tag lists (a sent list replaces that kind; the server maintains
      // the legacy flat Artist/Genre cache from these — no flat fields sent).
      artists: form.artists,
      genres: form.genres,
      tags: form.tags,
    };
    try {
      const saved = songId
        ? await apiSongUpdate(songId, payload)
        : await apiSongCreate(payload);
      pushToast('Song saved.');
      setInFlight(false);
      // Proceed with the navigation the user requested (blocker is released
      // once inFlight clears); if none was pending, go to the song view.
      if (blocker.state === 'blocked') {
        blocker.proceed?.();
      } else {
        navigate(`/songs/${saved.id}`);
      }
    } catch (e: unknown) {
      setInFlight(false);
      if (e instanceof ApiError) {
        setFormError(e.message);
        if (e.fields) {
          const errs: FieldErrors = {};
          for (const [k, v] of Object.entries(e.fields)) {
            if (v) {
              errs[k as keyof FormState] = v;
            }
          }
          setFieldErrors((prev) => ({ ...prev, ...errs }));
        }
      } else {
        setFormError(e instanceof Error ? e.message : 'Save failed.');
      }
    }
  }

  // ST-106: re-run the server chord analyzer on the saved chart (POST detect-key
  // persists: key = detected, override cleared). Re-baselines the dirty tracker.
  async function detectKey() {
    if (!songId || inFlight || detecting) return;
    setDetecting(true);
    try {
      const d = await apiDetectKey(songId);
      const next = { ...form, key: d.key };
      setForm(next);
      setInitial(JSON.stringify(next));
      setKeyMeta({ override: false, confidence: d.confidence });
      pushToast(`Detected ${d.key} from the chords (${Math.round(d.confidence * 100)}% match).`);
    } catch (e: unknown) {
      pushToast(e instanceof Error ? e.message : 'Key detection failed.');
    } finally {
      setDetecting(false);
    }
  }

  async function doDelete() {
    if (!songId || inFlight) return;
    setInFlight(true);
    try {
      await apiSongDelete(songId);
      pushToast('Song deleted.');
      if (blocker.state === 'blocked') blocker.proceed?.();
      else navigate('/');
    } catch (e: unknown) {
      setInFlight(false);
      setFormError(e instanceof Error ? e.message : 'Delete failed.');
    }
  }

  if (loading) {
    return (
      <div className={styles.page} aria-busy="true">
        <div className={ui.skeleton} style={{ height: 300 }} />
      </div>
    );
  }

  return (
    <div className={styles.page}>
      <div className={styles.head}>
        <h1 className={styles.h1}>{mode === 'new' ? 'New song' : 'Edit song'}</h1>
        <div className={styles.headActions}>
          {mode === 'edit' && (
            // ST-35 D2: bin icon on the delete trigger.
            <button type="button" className={`${ui.btn} ${ui.danger} ${ui.small}`} onClick={() => setConfirmDelete(true)}>
              {/* ST-36 E1: flat trash icon (was emoji). */}
              <TrashIcon size={18} /> Delete
            </button>
          )}
          <button type="button" className={`${ui.btn} ${ui.primary}`} onClick={save} disabled={inFlight}>
            {inFlight ? 'Saving…' : 'Save'}
          </button>
        </div>
      </div>

      {formError && (
        <div className={ui.errorBanner} role="alert">
          <span>{formError}</span>
        </div>
      )}
      {dirty && <p className={styles.dirtyHint}>Unsaved changes</p>}

      <div className={`${styles.columns} ${showPreview ? '' : styles.solo}`}>
        <form
          className={styles.formCol}
          onSubmit={(e) => {
            e.preventDefault();
            void save();
          }}
        >
          <div className={styles.metaGrid}>
            <div className={styles.mbRow}>
              <TextField
                label="Title *"
                value={form.title}
                onChange={(v) => set({ title: v })}
                error={fieldErrors.title}
                placeholder="Song title"
              />
              <button
                type="button"
                className={`${ui.btn} ${ui.small} ${styles.mbBtn}`}
                onClick={() => void lookupMusicBrainz()}
                disabled={mbLoading}
              >
                {mbLoading ? 'Looking up…' : 'Look up'}
              </button>
            </div>
            {mbError && (
              <p className={styles.mbError} role="alert">
                {mbError}
              </p>
            )}
            {mbResults && (
              <ul className={styles.mbList}>
                {mbResults.length === 0 && <li className={ui.muted}>No results.</li>}
                {mbResults.map((r, i) => (
                  <li key={`${r.mbid}-${i}`}>
                    <button type="button" className={styles.mbItem} onClick={() => applyMb(r)}>
                      <strong>{r.title}</strong>
                      <span className={ui.muted}>
                        {[r.artist, r.album, r.year].filter(Boolean).join(' · ')}
                      </span>
                    </button>
                  </li>
                ))}
              </ul>
            )}
            {/* ST-106: multi-artist chip input — chip order is the saved display order. */}
            <TagInput
              label="Artists"
              kind="artist"
              value={form.artists}
              onChange={(artists) => set({ artists })}
              placeholder="Add an artist, Enter"
              reorderable
              hint="Enter/commas add · ↑↓ set the display order (a band name with “&” stays one chip)"
            />
            <TextField
              label="Album"
              value={form.album}
              onChange={(v) => set({ album: v })}
              placeholder="Album (optional)"
            />
            <TextField
              label="Year"
              value={form.year}
              onChange={(v) => set({ year: v.replace(/[^0-9]/g, '') })}
              error={fieldErrors.year}
              placeholder="e.g. 1970"
              type="text"
              inputMode="numeric"
            />
            {/* ST-106: multi-genre chips (server title-cases: pop → Pop) + free tags. */}
            <TagInput
              label="Genres"
              kind="genre"
              value={form.genres}
              onChange={(genres) => set({ genres })}
              placeholder="Add a genre, Enter"
              hint="Names are normalised per word: “pop rock” saves as “Pop Rock”"
            />
            <TagInput
              label="Tags"
              kind="tag"
              value={form.tags}
              onChange={(tags) => set({ tags })}
              placeholder="Add a tag, Enter — e.g. stage, prayer, outreach"
            />
            <TextField
              label="Tempo (BPM)"
              value={form.tempo}
              onChange={(v) => set({ tempo: v.replace(/[^0-9]/g, '') })}
              error={fieldErrors.tempo}
              placeholder="e.g. 120"
              type="text"
              inputMode="numeric"
            />
            <div className={styles.keyRow}>
              <label className={styles.keyField}>
                <span className={styles.label}>Key</span>
                <input
                  className={styles.input}
                  list="ahchord-keys"
                  value={form.key}
                  onChange={(e) => set({ key: e.target.value })}
                  placeholder="e.g. G"
                />
                <datalist id="ahchord-keys">
                  {KEYS.map((k) => (
                    <option key={k} value={k} />
                  ))}
                </datalist>
              </label>
              {mode === 'edit' && (
                // ST-106: re-run the chord analyzer on the saved chart.
                <button
                  type="button"
                  className={`${ui.btn} ${ui.small} ${styles.mbBtn}`}
                  onClick={() => void detectKey()}
                  disabled={detecting}
                >
                  {detecting ? 'Detecting…' : 'Detect from chords'}
                </button>
              )}
            </div>
            {/* ST-106: where the key came from — hand-set (override) vs chord-derived. */}
            <p className={styles.keyHint}>
              {form.key.trim() === ''
                ? 'Empty key = derived from the chords on save.'
                : form.key.trim() !== (songId ? initialKey : '')
                  ? 'Saving sets this key by hand (an override — re-detecting replaces it).'
                  : keyMeta.override
                    ? 'Hand-set: saving keeps it, the chord analyzer will not replace it. Clear the field to hand the key back to the chords.'
                    : keyMeta.confidence != null
                      ? `From the chords (${Math.round(keyMeta.confidence * 100)}% match). Typing a new key makes it an override.`
                      : 'Saving sets this key by hand (an override).'}
            </p>
            <label className={styles.keyField}>
              <span className={styles.label}>Capo (default)</span>
              <select
                className={styles.input}
                value={form.capoFret}
                onChange={(e) => set({ capoFret: Number(e.target.value) })}
              >
                {[0, 1, 2, 3, 4, 5, 6].map((f) => (
                  <option key={f} value={f}>
                    {f === 0 ? '0 (as written)' : `Fret ${f}`}
                  </option>
                ))}
              </select>
            </label>
            <TextField
              label="Source URL"
              value={form.sourceUrl}
              onChange={(v) => set({ sourceUrl: v })}
              placeholder="https://…"
            />
          </div>

          <div className={styles.editorBlock}>
            <div className={styles.editorHead}>
              <span className={styles.label}>
                ChordPro text *{' '}
                <span className={ui.muted}>
                  ({lineCount} line{lineCount === 1 ? '' : 's'} · Tab inserts 2 spaces)
                </span>
              </span>
              <button
                type="button"
                className={`${ui.btn} ${ui.small}`}
                onClick={() => setShowPreview((p) => !p)}
                aria-pressed={showPreview}
              >
                {showPreview ? 'Hide preview' : 'Show preview'}
              </button>
            </div>
            <textarea
              className={styles.editor}
              value={form.chordProText}
              onChange={(e) => set({ chordProText: e.target.value })}
              onKeyDown={onTab}
              placeholder={'{title: My Song}\n{key: C}\n\n[C] Hello [G] world\n[Am] Goodbye [F] moon'}
              aria-label="ChordPro text"
              aria-invalid={fieldErrors.chordProText ? true : undefined}
            />
            {fieldErrors.chordProText && (
              <p className={styles.fieldError} role="alert">
                {fieldErrors.chordProText}
              </p>
            )}
          </div>
        </form>

        {/* Bug report #98: the Hide/Show preview button actually hides the preview
            column (the toggle used to only flip its own label). */}
        {showPreview && (
          <aside className={styles.previewCol} aria-label="Preview">
            <h2 className={styles.previewTitle}>Preview</h2>
            {preview ? (
              <SongRenderer parsed={preview} />
            ) : (
              <p className={ui.muted}>No chord content detected yet.</p>
            )}
          </aside>
        )}
      </div>

      {blocker.state === 'blocked' && (
        <div className={styles.overlay} role="dialog" aria-modal="true" aria-label="Unsaved changes">
          <div className={styles.dialog}>
            <h2>Unsaved changes</h2>
            <p className={ui.muted}>
              You have unsaved changes. Save before leaving, or discard them.
            </p>
            <div className={styles.dialogActions}>
              <button
                type="button"
                className={ui.btn}
                onClick={() => {
                  blocker.reset?.();
                }}
              >
                Keep editing
              </button>
              <button
                type="button"
                className={`${ui.btn} ${ui.danger}`}
                onClick={() => {
                  setInitial(JSON.stringify(form));
                  blocker.proceed?.();
                }}
              >
                Discard & leave
              </button>
            </div>
          </div>
        </div>
      )}

      {confirmDelete && (
        <div className={styles.overlay} role="dialog" aria-modal="true" aria-label="Delete song">
          <div className={styles.dialog}>
            <h2>Delete “{form.title}”?</h2>
            {/* ST-35 D3: plain confirm/cancel — no typed-title requirement. */}
            <p className={ui.muted}>This removes the song from the library and every playlist.</p>
            <div className={styles.dialogActions}>
              <button
                type="button"
                className={ui.btn}
                onClick={() => setConfirmDelete(false)}
              >
                Cancel
              </button>
              <button
                type="button"
                className={`${ui.btn} ${ui.danger}`}
                disabled={inFlight}
                onClick={() => void doDelete()}
              >
                {inFlight ? 'Deleting…' : 'Delete song'}
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
