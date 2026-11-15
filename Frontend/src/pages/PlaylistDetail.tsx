import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import {
  apiPlaylist,
  apiPlaylistAddSong,
  apiPlaylistRemoveSong,
  apiPlaylistReorder,
  apiPlaylistUpdate,
  apiSongs,
} from '../api/client';
import type { PlaylistDetail as PlaylistDetailDto, Song } from '../api/types';
import { isNetworkError } from '../offline/fallback';
import { offlinePlaylist, offlineSongSearch } from '../offline/query';
import { useToastStore } from '../stores/toast';
import { GripIcon } from '../components/Icons';
import { RowMenu } from '../components/RowMenu';
import { TagInput } from '../components/TagInput';
import ui from '../components/ui.module.css';
import styles from './PlaylistDetail.module.css';

/**
 * Playlist detail (ST-23): inline rename, description, setlist badge,
 * Play setlist, ordered song list with up/down reordering (optimistic +
 * rollback), add-songs search (already-added disabled), setlist tags (ST-106,
 * replacing the collection assignment).
 */
export default function PlaylistDetail() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const pushToast = useToastStore((s) => s.push);

  const [data, setData] = useState<PlaylistDetailDto | null>(null);
  const [error, setError] = useState<string | null>(null);

  const [editingName, setEditingName] = useState(false);
  const [nameDraft, setNameDraft] = useState('');
  const [descDraft, setDescDraft] = useState('');
  // Bug report #99: editable gig date ("yyyy-MM-dd"; "" clears).
  const [gigDraft, setGigDraft] = useState('');

  // Bug report #100: the left transfer column — the library (page 1, 50 songs),
  // filtered by the search box; the right column is the playlist's own list.
  const [addQuery, setAddQuery] = useState('');
  const [addResults, setAddResults] = useState<Song[] | null>(null);
  const [adding, setAdding] = useState(false);

  const load = useCallback(async () => {
    if (!id) return;
    try {
      const pl = await apiPlaylist(id);
      setData(pl);
      setDescDraft(pl.description ?? '');
      setGigDraft(pl.gigDate ? pl.gigDate.slice(0, 10) : '');
      setError(null);
    } catch (e) {
      // MT-04: offline → the cached playlist (read-only — edits need the server).
      if (isNetworkError(e)) {
        const pl = await offlinePlaylist(id);
        if (pl) {
          setData(pl);
          setDescDraft(pl.description ?? '');
          setGigDraft(pl.gigDate ? pl.gigDate.slice(0, 10) : '');
          setError(null);
          return;
        }
      }
      setError(e instanceof Error ? e.message : 'Could not load the playlist.');
    }
  }, [id]);

  useEffect(() => {
    void load();
  }, [load]);

  // Bug report #100: the left column lists the library — a plain page on load
  // (first 50), a debounced search as you type. ST-64: run token pattern.
  const addRun = useRef(0);
  useEffect(() => {
    const q = addQuery.trim();
    const run = ++addRun.current;
    const t = window.setTimeout(
      () => {
        apiSongs(q, 1, 50)
          .then((r) => {
            if (run === addRun.current) setAddResults(r);
          })
          .catch(async () => {
            // MT-04: offline → search the cached library (adding needs the server).
            const r = await offlineSongSearch(q, 50).catch(() => null);
            if (run === addRun.current) setAddResults(r);
          });
      },
      q ? 300 : 0,
    );
    return () => window.clearTimeout(t);
  }, [addQuery]);

  const songIds = useMemo(() => new Set((data?.songs ?? []).map((s) => s.id)), [data]);

  async function saveMeta() {
    if (!id || !data) return;
    try {
      await apiPlaylistUpdate(id, {
        name: nameDraft.trim() || undefined,
        description: descDraft, // empty string clears it server-side
        gigDate: gigDraft, // bug report #99: "yyyy-MM-dd" / "" clears
      });
      setEditingName(false);
      pushToast('Playlist updated.');
      await load();
    } catch (e) {
      pushToast(e instanceof Error ? e.message : 'Update failed.', 'error');
    }
  }

  // Bug report (2026-11-12, /playlists): the setlist toggle is gone — every playlist
  // is a setlist (plays in order in the full-screen player, syncs the band).
  async function addSong(songId: string) {
    if (!id) return;
    setAdding(true);
    try {
      await apiPlaylistAddSong(id, { songId });
      pushToast('Song added.');
      setAddQuery('');
      setAddResults(null);
      await load();
    } catch (e) {
      pushToast(e instanceof Error ? e.message : 'Could not add the song.', 'error');
    } finally {
      setAdding(false);
    }
  }

  async function removeSong(songId: string) {
    if (!id) return;
    try {
      await apiPlaylistRemoveSong(id, songId);
      pushToast('Song removed from playlist.');
      await load();
    } catch (e) {
      pushToast(e instanceof Error ? e.message : 'Could not remove the song.', 'error');
    }
  }

  /**
   * Optimistic reorder: apply locally, PUT the full order, roll back on error.
   * ST-64: all reorder math composes on `songsRef` (kept in sync with `data` on commit),
   * and the rollback applies the INVERSE SWAP by song id to the current order — the old
   * render-closure snapshot rollback restored the pre-first-move list on a failure and
   * silently discarded a successful second move made in the meantime.
   */
  const songsRef = useRef<Song[] | null>(null);
  useEffect(() => {
    songsRef.current = data?.songs ?? null;
  }, [data]);

  async function move(index: number, dir: -1 | 1) {
    if (!id) return;
    const songs = songsRef.current;
    if (!songs) return;
    const target = index + dir;
    if (target < 0 || target >= songs.length) return;

    const a = songs[index];
    const b = songs[target];
    const next = [...songs];
    [next[index], next[target]] = [next[target], next[index]];
    songsRef.current = next;
    setData((d) => (d ? { ...d, songs: next } : d));

    try {
      await apiPlaylistReorder(
        id,
        next.map((s) => s.id),
      );
    } catch (e) {
      const cur = songsRef.current;
      if (cur) {
        const ia = cur.findIndex((s) => s.id === a.id);
        const ib = cur.findIndex((s) => s.id === b.id);
        if (ia >= 0 && ib >= 0) {
          const rolled = [...cur];
          [rolled[ia], rolled[ib]] = [rolled[ib], rolled[ia]];
          songsRef.current = rolled;
          setData((d) => (d ? { ...d, songs: rolled } : d));
        }
      }
      pushToast(e instanceof Error ? e.message : 'Reorder failed — reverted.', 'error');
    }
  }

  // ST-75: drag-to-reorder — the grip starts the drag, rows live-swap while
  // dragging, and the full order is PUT at drag end (optimistic, same pattern
  // as `move`; a failure reloads from the server).
  const [dragFrom, setDragFrom] = useState<number | null>(null);
  // ST-75 review: remember where the drag started — a drag that swaps nothing
  // must not PUT the full order.
  const dragStartIndex = useRef<number | null>(null);

  function onDragOverRow(i: number) {
    if (dragFrom === null || dragFrom === i) return;
    const cur = songsRef.current;
    if (!cur) return;
    const next = [...cur];
    const [moved] = next.splice(dragFrom, 1);
    next.splice(i, 0, moved);
    songsRef.current = next;
    setData((d) => (d ? { ...d, songs: next } : d));
    setDragFrom(i);
  }

  function onDragEnd() {
    const moved =
      dragStartIndex.current !== null && dragFrom !== null && dragStartIndex.current !== dragFrom;
    dragStartIndex.current = null;
    setDragFrom(null);
    const cur = songsRef.current;
    if (!id || !cur || !moved) return;
    apiPlaylistReorder(
      id,
      cur.map((s) => s.id),
    ).catch((e: unknown) => {
      pushToast(e instanceof Error ? e.message : 'Reorder failed — reloaded.', 'error');
      void load();
    });
  }

  // ST-106: setlist tags (the collection retirement) — every chip change saves.
  async function saveTags(tags: string[]) {
    if (!id) return;
    setData((d) => (d ? { ...d, tags } : d)); // optimistic; reload rolls back on failure
    try {
      await apiPlaylistUpdate(id, { tags });
    } catch (e) {
      pushToast(e instanceof Error ? e.message : 'Tag update failed — reloaded.', 'error');
      await load();
    }
  }

  if (error) {
    return (
      <div className={ui.errorBanner} role="alert">
        <span>{error}</span>
        <button type="button" className={`${ui.btn} ${ui.small}`} onClick={() => void load()}>
          Retry
        </button>
      </div>
    );
  }

  if (!data) {
    return <div className={ui.skeleton} style={{ height: 300 }} aria-busy="true" />;
  }

  return (
    <div className={styles.page}>
      <div className={styles.head}>
        <div>
          {editingName ? (
            <div className={styles.nameEdit}>
              <input
                className={styles.input}
                value={nameDraft}
                onChange={(e) => setNameDraft(e.target.value)}
                aria-label="Playlist name"
              />
              <button
                type="button"
                className={`${ui.btn} ${ui.small} ${ui.primary}`}
                onClick={() => void saveMeta()}
              >
                Save
              </button>
            </div>
          ) : (
            <h1 className={styles.h1}>{data.name}</h1>
          )}
          <textarea
            className={styles.desc}
            value={descDraft}
            onChange={(e) => setDescDraft(e.target.value)}
            placeholder="Add a description…"
            aria-label="Playlist description"
            rows={2}
          />
          {/* Bug report #99: the gig date, saved on blur — the dashboard surfaces
              the playlist as the date nears. */}
          <label className={styles.metaField}>
            <span className={ui.muted}>Gig date</span>
            <input
              className={styles.input}
              type="date"
              value={gigDraft}
              onChange={(e) => setGigDraft(e.target.value)}
              onBlur={() => {
                if (gigDraft !== (data.gigDate ?? '').slice(0, 10)) void saveMeta();
              }}
              aria-label="Gig date"
            />
          </label>
          <div className={styles.actions}>
            {/* Bug report (2026-11-12): every playlist is a setlist — the play button
                shows whenever there are songs (no setlist flag, no toggle). */}
            {data.songs.length > 0 && (
              <button
                type="button"
                className={`${ui.btn} ${ui.primary}`}
                onClick={() => navigate(`/playlists/${data.id}/player`)}
              >
                ▶ Play setlist
              </button>
            )}
            <button
              type="button"
              className={ui.btn}
              onClick={() => {
                setNameDraft(data.name);
                setEditingName(true);
              }}
            >
              Rename
            </button>
            {/* ST-106: setlist tags replace the collection assignment — free tags
                with autocomplete; each chip change saves immediately. */}
            <div className={styles.tagsField}>
              <TagInput
                label="Tags"
                kind="tag"
                value={data.tags ?? []}
                onChange={(tags) => void saveTags(tags)}
                placeholder="Add a tag, Enter — e.g. Sunday, Worship"
              />
            </div>
          </div>
        </div>
      </div>

      {/* Bug report #100: two columns — the library on the left, the playlist's
          songs on the right; every row moves with a single button. */}
      <section className={styles.transfer} aria-label="Songs">
        <div className={styles.pane}>
          <h2 className={styles.paneTitle}>All songs</h2>
          <input
            className={styles.input}
            value={addQuery}
            onChange={(e) => setAddQuery(e.target.value)}
            placeholder="Search the library…"
            aria-label="Search songs to add"
            type="search"
          />
          <ul className={styles.paneList}>
            {addResults === null && <li className={ui.muted}>Loading…</li>}
            {addResults && addResults.filter((s) => !songIds.has(s.id)).length === 0 && (
              <li className={ui.muted}>
                Nothing left to add{addQuery ? ' for this search.' : '.'}
              </li>
            )}
            {addResults
              ?.filter((s) => !songIds.has(s.id))
              .map((s) => (
                <li key={s.id} className={styles.paneRow}>
                  <span className={styles.songInfo}>
                    <strong>{s.title}</strong>
                    {s.artist && <span className={ui.muted}> — {s.artist}</span>}
                  </span>
                  <button
                    type="button"
                    className={`${ui.btn} ${ui.small}`}
                    disabled={adding}
                    onClick={() => void addSong(s.id)}
                    title="Add to this playlist"
                  >
                    Add →
                  </button>
                </li>
              ))}
          </ul>
          {addResults && !addQuery && addResults.length >= 50 && (
            <p className={ui.muted}>Showing the first 50 songs — search to find more.</p>
          )}
        </div>

        <div className={styles.pane}>
          <h2 className={styles.paneTitle}>
            In this playlist <span className={ui.muted}>({data.songs.length})</span>
          </h2>
          {data.songs.length === 0 ? (
            <p className={ui.muted}>Empty — add songs from the left.</p>
          ) : (
            <ol className={styles.paneList}>
              {data.songs.map((s, i) => (
                <li
                  key={s.id}
                  className={`${styles.paneRow} ${dragFrom === i ? styles.dragging : ''}`}
                  onDragOver={(e) => {
                    e.preventDefault();
                    onDragOverRow(i);
                  }}
                  onDrop={(e) => e.preventDefault()}
                >
                  <span className={styles.pos}>{i + 1}</span>
                  {/* ST-75: the grip starts an HTML5 drag (handle-only, so text
                      selection stays free); keyboard reorder lives in the ⋯ menu. */}
                  <button
                    type="button"
                    className={styles.grip}
                    draggable
                    onDragStart={(e) => {
                      setDragFrom(i);
                      dragStartIndex.current = i;
                      e.dataTransfer.effectAllowed = 'move';
                      e.dataTransfer.setData('text/plain', String(i));
                    }}
                    onDragEnd={onDragEnd}
                    aria-label={`Drag to reorder ${s.title}`}
                    title="Drag to reorder"
                  >
                    <GripIcon size={16} />
                  </button>
                  <Link to={`/songs/${s.id}`} className={styles.songInfo}>
                    <strong>{s.title}</strong>
                    {s.artist && <span className={ui.muted}> — {s.artist}</span>}
                  </Link>
                  <span className={styles.songActions}>
                    {/* ST-75: labeled actions (was bare ↑ ↓ × glyphs). */}
                    <RowMenu
                      label={`Actions for ${s.title}`}
                      items={[
                        {
                          key: 'up',
                          label: 'Move up',
                          disabled: i === 0,
                          onSelect: () => void move(i, -1),
                        },
                        {
                          key: 'down',
                          label: 'Move down',
                          disabled: i === data.songs.length - 1,
                          onSelect: () => void move(i, 1),
                        },
                        {
                          key: 'remove',
                          label: 'Remove from playlist',
                          danger: true,
                          onSelect: () => void removeSong(s.id),
                        },
                      ]}
                    />
                    <button
                      type="button"
                      className={`${ui.btn} ${ui.small}`}
                      disabled={adding}
                      onClick={() => void removeSong(s.id)}
                      title="Remove from this playlist"
                    >
                      ← Remove
                    </button>
                  </span>
                </li>
              ))}
            </ol>
          )}
        </div>
      </section>
    </div>
  );
}
