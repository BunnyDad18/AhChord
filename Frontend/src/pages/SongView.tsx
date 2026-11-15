import { useCallback, useEffect, useRef, useState } from 'react';
import type { CSSProperties } from 'react';
import { Link, useParams } from 'react-router-dom';
import {
  apiDeleteSourceFile,
  apiSong,
  apiSongSourceFiles,
  apiSongUpdate,
  apiSongView,
  apiSongVersions,
  apiSourceFileBlob,
  apiUploadSourceFile,
} from '../api/client';
import { ApiError } from '../api/types';
import type { Song, SongDetailResponse, SongSourceFile } from '../api/types';
import { offlineAware } from '../offline/fallback';
import { offlineSongDetail, offlineVersions, recordView } from '../offline/query';
import { useToastStore } from '../stores/toast';
import { useTransposeStore } from '../stores/transpose';
import { publishFollow } from '../realtime/followClient';
import { KeyCapoControl } from '../components/KeyCapoControl';
import { effectiveKey, formatTranspose } from '../components/TransposeControl';
import { recordLastPlay } from '../utils/lastPlay';
import { ChordName } from '../components/ChordName';
import { ColumnsControl } from '../components/ColumnsControl';
import { SongRenderer } from '../components/SongRenderer';
import { useColumnPref } from '../utils/columns';
import ui from '../components/ui.module.css';
import styles from './SongView.module.css';

/**
 * ST-106: the artist display line — first two artists spelled out, the rest
 * collapsed into a "+N" tail (five credited writers don't crowd the header).
 */
function artistLine(artists: string[]): Array<{ name: string; more?: number }> {
  if (artists.length <= 3) return artists.map((name) => ({ name }));
  return [{ name: artists[0] }, { name: artists[1] }, { name: '', more: artists.length - 2 }];
}

type State =
  | { status: 'loading' }
  | { status: 'notfound' }
  | { status: 'error'; message: string }
  | { status: 'ready'; data: SongDetailResponse };

/**
 * Song view (ST-18): header (title, artist, key, transpose control, Edit, Play)
 * plus the parsed chart with chord tokens above their lyrics. Transpose is session
 * state (-12…+12, ST-39 E1) — changing it refetches ?capo=N; the stored song never changes.
 * ST-46 bug-56: attached source files (pdf/png/jpg) with inline preview below the header.
 */
export default function SongView() {
  const { id } = useParams<{ id: string }>();
  const transpose = useTransposeStore((s) => s.transpose);
  // Stable identity so SongRenderer's memo (ST-28) can skip re-renders.
  const renderChord = useCallback((name: string) => <ChordName name={name} />, []);
  const [state, setState] = useState<State>({ status: 'loading' });
  const [loadedId, setLoadedId] = useState<string | null>(null);
  const [reloadKey, setReloadKey] = useState(0);

  // ST-75: opening a song feeds the dashboard's "Jump back in" hero (the player
  // does the same on play; this covers reading charts on the song page).
  useEffect(() => {
    if (state.status !== 'ready') return;
    recordLastPlay({
      type: 'song',
      id: state.data.song.id,
      title: state.data.song.title,
      artist: state.data.song.artist,
    });
  }, [state]);

  // ST-35 F1: other imported versions of this song (same title+artist), oldest first.
  const [versions, setVersions] = useState<Song[] | null>(null);

  // ST-46 bug-56: attached source files (pdf/png/jpg) with inline preview.
  const [sourceFiles, setSourceFiles] = useState<SongSourceFile[] | null>(null);
  const [uploading, setUploading] = useState(false);
  const [preview, setPreview] = useState<{ file: SongSourceFile; url: string } | null>(null);
  const sourceFileRef = useRef<HTMLInputElement>(null);
  const pushToast = useToastStore((s) => s.push);

  // ST-39 F2: lyrics font scale (0.8–1.6), persisted per browser.
  const [fontScale, setFontScale] = useState(() => {
    const v = Number(window.localStorage.getItem('ahchord.songFontScale'));
    return Number.isFinite(v) && v >= 0.8 && v <= 1.6 ? v : 1;
  });

  // ST-39 F3: chord display mode (inline | above), persisted per browser.
  const [chordMode, setChordMode] = useState<'inline' | 'above'>(
    () => (window.localStorage.getItem('ahchord.chordMode') === 'above' ? 'above' : 'inline'),
  );

  // ST-42: column layout preference (auto | 1 | 2 | 3), persisted per browser, shared with the player.
  const [columns, setColumns] = useColumnPref();

  function bumpFont(delta: number) {
    setFontScale((s) => Math.max(0.8, Math.min(1.6, Math.round((s + delta) * 10) / 10)));
  }

  // ST-75 review: persist in an effect — updaters run twice under StrictMode.
  useEffect(() => {
    window.localStorage.setItem('ahchord.songFontScale', String(fontScale));
  }, [fontScale]);

  function pickChordMode(mode: 'inline' | 'above') {
    setChordMode(mode);
    window.localStorage.setItem('ahchord.chordMode', mode);
  }

  // ST-46 bug-56: attach / preview / download / delete source files.
  async function uploadSourceFile(file: File | null | undefined) {
    if (!file || !id) return;
    setUploading(true);
    try {
      const created = await apiUploadSourceFile(id, file);
      setSourceFiles((prev) => [...(prev ?? []), created]);
      pushToast(`Attached “${created.fileName}”.`);
    } catch (e: unknown) {
      pushToast(e instanceof Error ? e.message : 'Upload failed.', 'error');
    } finally {
      setUploading(false);
      if (sourceFileRef.current) sourceFileRef.current.value = '';
    }
  }

  async function openPreview(file: SongSourceFile) {
    try {
      const blob = await apiSourceFileBlob(file.id);
      setPreview({ file, url: URL.createObjectURL(blob) });
    } catch (e: unknown) {
      pushToast(e instanceof Error ? e.message : 'Could not load the file.', 'error');
    }
  }

  async function downloadSourceFile(file: SongSourceFile) {
    try {
      const blob = await apiSourceFileBlob(file.id);
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = file.fileName;
      a.click();
      window.setTimeout(() => URL.revokeObjectURL(url), 1000);
    } catch (e: unknown) {
      pushToast(e instanceof Error ? e.message : 'Download failed.', 'error');
    }
  }

  // ST-51 #6: the "source files" section is for attached pdf/png/jpg, so make the
  // ChordPro text itself retrievable from here too — download it as a .cho file.
  function downloadChordPro() {
    if (state.status !== 'ready') return;
    const song = state.data.song;
    const name = song.title.replace(/[^\p{L}\p{N}]+/gu, '-').replace(/^-+|-+$/g, '') || 'song';
    const blob = new Blob([song.chordProText], { type: 'text/plain' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `${name}.cho`;
    a.click();
    window.setTimeout(() => URL.revokeObjectURL(url), 1000);
  }

  async function deleteSourceFile(file: SongSourceFile) {
    try {
      await apiDeleteSourceFile(file.id);
      setSourceFiles((prev) => prev?.filter((f) => f.id !== file.id) ?? null);
      if (preview?.file.id === file.id) setPreview(null);
      pushToast(`Removed “${file.fileName}”.`);
    } catch (e: unknown) {
      pushToast(e instanceof Error ? e.message : 'Delete failed.', 'error');
    }
  }

  // ST-51 #8: persist the song's stored capo (0–6) — optimistic update, rollback on failure.
  // ST-64: functional updates (Player's pattern) — a transpose re-fetch can replace the data
  // between the optimistic write and a failed PUT; the old closure-snapshot rollback
  // reinstated the OLD chart content then. Only capoFret is restored, on current data.
  async function commitCapo(fret: number) {
    if (!id || state.status !== 'ready') return;
    const prev = state.data.song.capoFret;
    setState((s) =>
      s.status === 'ready'
        ? { status: 'ready', data: { ...s.data, song: { ...s.data.song, capoFret: fret } } }
        : s,
    );
    try {
      await apiSongUpdate(id, { capoFret: fret });
      // ST-77 #122: the shapes shown = chart shifted by (transpose − capo) — a new capo
      // means a new projection, so refetch the chart (same reloadKey the retry uses).
      setReloadKey((k) => k + 1);
    } catch (e: unknown) {
      setState((s) =>
        s.status === 'ready'
          ? { status: 'ready', data: { ...s.data, song: { ...s.data.song, capoFret: prev } } }
          : s,
      );
      pushToast(e instanceof Error ? e.message : 'Could not save the capo.', 'error');
    }
  }

  function formatSize(bytes: number): string {
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
    return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  }

  // Loading for the *current* song: skeleton while the song we're showing is
  // not this one. A transpose re-fetch keeps the previous content visible (no flash).
  const isLoading =
    !!id && (state.status === 'loading' || (state.status === 'ready' && loadedId !== id));

  // Fire-and-forget view history (feeds /api/songs/recent).
  useEffect(() => {
    if (!id) return;
    recordView(id); // MT-04: local copy — Recent works offline too
    apiSongView(id).catch(() => {
      /* best effort */
    });
  }, [id]);

  // ST-35 F1: fetch sibling versions when the song id changes (best effort — hidden on failure).
  useEffect(() => {
    if (!id) return;
    let cancelled = false;
    apiSongVersions(id)
      .then((v) => {
        if (!cancelled) setVersions(v);
      })
      .catch(async () => {
        // MT-04: offline → sibling versions from the cached library.
        const cached = await offlineVersions(id).catch(() => null);
        if (!cancelled) setVersions(cached);
      });
    return () => {
      cancelled = true;
    };
  }, [id]);

  // ST-46 bug-56: fetch attached source files when the song id changes (best effort).
  useEffect(() => {
    if (!id) return;
    let cancelled = false;
    setSourceFiles(null);
    apiSongSourceFiles(id)
      .then((f) => {
        if (!cancelled) setSourceFiles(f);
      })
      .catch(() => {
        if (!cancelled) setSourceFiles(null);
      });
    return () => {
      cancelled = true;
    };
  }, [id]);

  // Revoke the preview object URL when it changes or on unmount.
  useEffect(() => {
    return () => {
      if (preview) URL.revokeObjectURL(preview.url);
    };
  }, [preview]);

  // ST-51 #3: the transpose value belongs to this song — load its stored value before
  // fetching so we never fetch with a previous song's offset. The ref dedupes the
  // re-run that setForSong triggers when the loaded value differs from the current one.
  // ST-77 #122: a committed capo change bumps reloadKey — the projection shift is
  // (transpose − capo), so the chart refetches with the new shift.
  const fetchedRef = useRef<{ id: string; t: number; k: number } | null>(null);
  useEffect(() => {
    if (!id) return;
    useTransposeStore.getState().setForSong(id);
    const t = useTransposeStore.getState().transpose;
    if (fetchedRef.current?.id === id && fetchedRef.current.t === t && fetchedRef.current.k === reloadKey) return;
    fetchedRef.current = { id, t, k: reloadKey };
    let cancelled = false;
    // MT-04: offline → parse + transpose the cached ChordPro (the server's projection, client-side).
    offlineAware(() => apiSong(id, t), () => offlineSongDetail(id, t))
      .then((data) => {
        if (cancelled) return;
        setState({ status: 'ready', data });
        setLoadedId(id);
      })
      .catch((e: unknown) => {
        if (cancelled) return;
        if (e instanceof ApiError && e.status === 404) {
          setState({ status: 'notfound' });
        } else {
          setState({
            status: 'error',
            message: e instanceof Error ? e.message : 'Could not load the song.',
          });
        }
      });
    return () => {
      cancelled = true;
      // ST-62: clear the dedupe guard on cleanup — React 18 StrictMode's setup→cleanup→setup
      // would otherwise have setup 2 return early at the fetchedRef check, discard the
      // cancelled fetch's result, and leave the skeleton up forever (dev-only hang).
      fetchedRef.current = null;
    };
  }, [id, transpose, reloadKey]);

  // ST-87: hosting — when I open a song, my follow session's followers follow it.
  // (Fire-and-forget; publishFollow is a no-op while I'm not hosting. A hook —
  // must stay above the status early-returns.)
  useEffect(() => {
    if (state.status !== 'ready') return;
    void publishFollow({
      kind: 'song',
      songId: state.data.song.id,
      songTitle: state.data.song.title,
      songArtist: state.data.song.artist,
    });
    // state is 'ready' with fresh data on every song load (id changes re-run it).
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [state]);

  if (isLoading) {
    return (
      <div className={styles.page} aria-busy="true" aria-label="Loading song">
        <div className={ui.skeleton} style={{ height: 40, width: '60%' }} />
        <div className={ui.skeleton} style={{ height: 24, width: '30%', marginTop: 12 }} />
        <div className={ui.skeleton} style={{ height: 280, marginTop: 24 }} />
      </div>
    );
  }

  if (state.status === 'notfound') {
    return (
      <div className={styles.page}>
        <div className={`${ui.card} ${styles.center}`}>
          <h2>Song not found</h2>
          <p className={ui.muted}>
            It may have been deleted, or the link is wrong.{' '}
            <Link to="/">Back to the dashboard →</Link>
          </p>
        </div>
      </div>
    );
  }

  if (state.status === 'error') {
    return (
      <div className={styles.page}>
        <div className={ui.errorBanner} role="alert">
          <span>{state.message}</span>
          <button
            type="button"
            className={`${ui.btn} ${ui.small}`}
            onClick={() => {
              setState({ status: 'loading' });
              setReloadKey((k) => k + 1);
            }}
          >
            Retry
          </button>
        </div>
      </div>
    );
  }

  if (state.status !== 'ready') return null;
  const { song, parsed } = state.data;

  // ST-75: unique chords in first-appearance order — the legend strip.
  const chordLegend: string[] = [];
  for (const sec of parsed.sections) {
    for (const ln of sec.lines) {
      for (const c of ln.chords) {
        if (!chordLegend.includes(c.name)) chordLegend.push(c.name);
      }
    }
  }

  return (
    <div className={styles.page}>
      <header className={styles.header}>
        <div className={styles.titleBlock}>
          <h1 className={styles.title}>{song.title}</h1>
          {/* ST-106: multi-artist line — first two + "+N", each linked to its filter. */}
          {(song.artists?.length ? song.artists : song.artist ? [song.artist] : []).length >
            0 && (
            <p className={styles.artist}>
              {artistLine(song.artists?.length ? song.artists : [song.artist!]).map((a, i) => (
                <span key={`${a.name}-${i}`}>
                  {i > 0 && ', '}
                  {a.more ? (
                    <span className={ui.muted}>+{a.more}</span>
                  ) : (
                    <Link to={`/songs?artist=${encodeURIComponent(a.name)}`}>{a.name}</Link>
                  )}
                </span>
              ))}
            </p>
          )}
          <div className={styles.metaRow}>
            {/* ST-39 E1/E2 / ST-51 #8 / ST-77 (#116): the key moves with the transpose only;
                the capo moves the shapes (badge above) and keeps the key. */}
            {song.key && (
              <span className={ui.badge}>
                key {song.key}
                {song.capoFret > 0 ? ` · capo ${song.capoFret}` : ''}
                {transpose !== 0
                  ? ` · playing in ${effectiveKey(song.key, transpose)}`
                  : song.capoFret > 0
                    ? ''
                    : ' · as written'}
              </span>
            )}
            {!song.key && transpose !== 0 && (
              <span className={ui.badge}>{formatTranspose(transpose)}</span>
            )}
            {song.album && <span className={ui.muted}>{song.album}</span>}
            {song.year && <span className={ui.muted}>{song.year}</span>}
            {/* ST-106: genre chips (multi-valued, linked to the genre filter). */}
            {(song.genres?.length ? song.genres : song.genre ? [song.genre] : []).map((g) => (
              <Link key={g} to={`/songs?genre=${encodeURIComponent(g)}`} className={ui.badge}>
                {g}
              </Link>
            ))}
            {song.tempo && <span className={ui.muted}>{song.tempo} BPM</span>}
          </div>
          {/* ST-106: free tags — linked to the songs list narrowed by this tag. */}
          {!!song.tags?.length && (
            <div className={styles.metaRow}>
              {song.tags.map((t) => (
                <Link key={t} to={`/songs?tag=${encodeURIComponent(t)}`} className={ui.badge}>
                  #{t}
                </Link>
              ))}
            </div>
          )}
        </div>

        <div className={styles.actions}>
          <Link to={`/songs/${song.id}/edit`} className={ui.btn}>
            Edit
          </Link>
          <Link to={`/songs/${song.id}/player`} className={`${ui.btn} ${ui.primary}`}>
            ▶ Play
          </Link>
        </div>
      </header>

      {/* ST-75: the 13-control header row folds into groups — reading options
          (Inline/Above · columns · text size) left, Key & Capo (capo +
          transpose + play-in-key, showing the sounding key) right. */}
      <div className={styles.toolbar}>
        <div className={styles.readingControls}>
          <div className={styles.modeToggle} role="group" aria-label="Chord display mode">
            <button
              type="button"
              className={chordMode === 'inline' ? styles.modeActive : ''}
              onClick={() => pickChordMode('inline')}
            >
              Inline
            </button>
            <button
              type="button"
              className={chordMode === 'above' ? styles.modeActive : ''}
              onClick={() => pickChordMode('above')}
            >
              Above
            </button>
          </div>
          {/* ST-42: column layout (Auto / 1 / 2 / 3), shared with the player. */}
          <ColumnsControl value={columns} onChange={setColumns} />
          {/* ST-39 F2: lyrics font size. */}
          <div className={styles.modeToggle} role="group" aria-label="Lyrics font size">
            <button type="button" onClick={() => bumpFont(-0.1)} aria-label="Decrease lyrics font size">
              A−
            </button>
            <button type="button" onClick={() => bumpFont(0.1)} aria-label="Increase lyrics font size">
              A+
            </button>
          </div>
        </div>
        {/* ST-51 #8: capo (saved with the song) + transpose (session-only, on top of it). */}
        <KeyCapoControl
          songKey={song.key}
          capoFret={song.capoFret}
          onCommitCapo={(fret) => void commitCapo(fret)}
        />
      </div>

      {/* ST-75: chord legend — discoverability for the diagram tooltips (ST-20/21).
          Entries are real chord tokens: tapping one opens the diagram right there.
          (A div, not a p — the tooltip div must not sit inside a <p>.) */}
      {chordLegend.length > 0 && (
        <div className={styles.legend}>
          <span className={ui.muted}>
            {chordLegend.length} chord{chordLegend.length === 1 ? '' : 's'} in this song — tap one for the fingering:
          </span>{' '}
          <span className={styles.legendChords}>
            {chordLegend.map((c, i) => (
              <span key={c}>
                {i > 0 && <span className={ui.muted}> · </span>}
                <ChordName name={c} flat />
              </span>
            ))}
          </span>
        </div>
      )}

      {/* ST-35 F1: other imported versions of this song (same title+artist). */}
      {versions && versions.length > 1 && (
        <section className={styles.versions} aria-label="Other versions">
          <h2 className={styles.versionsTitle}>Other versions ({versions.length - 1})</h2>
          <ul className={styles.versionList}>
            {versions.map((v) => (
              <li key={v.id}>
                <Link to={`/songs/${v.id}`} className={styles.versionLink}>
                  Imported {new Date(v.createdAt).toLocaleDateString()}
                  {v.id === song.id ? ' — this version' : ''}
                </Link>
              </li>
            ))}
          </ul>
        </section>
      )}

      {/* ST-46 bug-56 + ST-75: attached source files collapse to a one-line bar
          (the old full card just to reach "Download .cho" was mostly empty space). */}
      {sourceFiles !== null && (
        <details className={styles.sourceFiles}>
          <summary className={styles.sourceSummary}>
            <span>
              Source files <span className={ui.muted}>({sourceFiles.length})</span>
            </span>
            <span className={styles.sourceBarActions}>
              {/* preventDefault: the buttons live in the summary — a click must not toggle. */}
              <button
                type="button"
                className={`${ui.btn} ${ui.small}`}
                onClick={(e) => {
                  e.preventDefault();
                  downloadChordPro();
                }}
              >
                Download .cho
              </button>
              <button
                type="button"
                className={`${ui.btn} ${ui.small}`}
                disabled={uploading}
                onClick={(e) => {
                  e.preventDefault();
                  sourceFileRef.current?.click();
                }}
              >
                {uploading ? 'Uploading…' : '+ Add file'}
              </button>
            </span>
          </summary>
          <input
            ref={sourceFileRef}
            type="file"
            accept=".pdf,.png,.jpg,.jpeg"
            hidden
            onChange={(e) => void uploadSourceFile(e.target.files?.[0])}
          />
          {sourceFiles.length === 0 ? (
            <p className={ui.muted}>
              No attached files yet — this section is for sheet music (pdf, png or jpg). The chart's ChordPro text lives in the app; use Download .cho to get it as a file.
            </p>
          ) : (
            <ul className={styles.sourceList}>
              {sourceFiles.map((f) => (
                <li key={f.id} className={styles.sourceRow}>
                  <span className={styles.sourceName}>{f.fileName}</span>
                  <span className={ui.muted}>{formatSize(f.sizeBytes)}</span>
                  <div className={styles.sourceActions}>
                    <button type="button" className={`${ui.btn} ${ui.small}`} onClick={() => void openPreview(f)}>
                      View
                    </button>
                    <button type="button" className={`${ui.btn} ${ui.small}`} onClick={() => void downloadSourceFile(f)}>
                      Download
                    </button>
                    <button type="button" className={`${ui.btn} ${ui.small}`} onClick={() => void deleteSourceFile(f)}>
                      Delete
                    </button>
                  </div>
                </li>
              ))}
            </ul>
          )}
          {preview && (
            <div className={styles.previewPane}>
              {preview.file.contentType.startsWith('image/') ? (
                <img className={styles.previewImg} src={preview.url} alt={preview.file.fileName} />
              ) : (
                <iframe className={styles.previewFrame} src={preview.url} title={preview.file.fileName} />
              )}
              <button type="button" className={`${ui.btn} ${ui.small}`} onClick={() => setPreview(null)}>
                Close preview
              </button>
            </div>
          )}
        </details>
      )}

      {/* ST-39 F1/F2 / ST-42: multi-column at any width; --lyric-scale drives the A−/A+ size.
          fontScale is passed through so the column measurement re-runs when it changes;
          layoutEpoch bumps when the async versions box appears above the chart (ST-42). */}
      <div className={styles.songBody} style={{ '--lyric-scale': String(fontScale) } as CSSProperties}>
        <SongRenderer
          parsed={parsed}
          renderChord={renderChord}
          chordMode={chordMode}
          multiColumn
          columns={columns}
          fontScale={fontScale}
          layoutEpoch={versions && versions.length > 1 ? 1 : 0}
        />
      </div>
    </div>
  );
}
