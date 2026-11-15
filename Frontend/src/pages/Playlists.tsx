import { useCallback, useRef, useState } from 'react';
import { Link, useNavigate, useSearchParams } from 'react-router-dom';
import { apiPlaylistCreate, apiPlaylistDelete, apiPlaylists } from '../api/client';
import { isNetworkError } from '../offline/fallback';
import { offlinePlaylists } from '../offline/query';
import type { Playlist } from '../api/types';
import { useToastStore } from '../stores/toast';
import { useDebouncedEffect } from '../hooks/useDebounce';
import { gigLabel } from '../utils/gigDate';
import { TrashIcon } from '../components/Icons';
import { RowMenu } from '../components/RowMenu';
import { LiveSessions } from '../components/LiveSessions';
import ui from '../components/ui.module.css';
import styles from './Playlists.module.css';

/**
 * Playlist list (ST-23): search filter, create (name + setlist flag),
 * delete (labeled ⋯ action, ST-75), tag deep-link filter
 * (/playlists?tag=name — ST-106, the collection-filter retirement).
 */

export default function Playlists() {
  const [playlists, setPlaylists] = useState<Playlist[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [search, setSearch] = useState('');
  const [name, setName] = useState('');
  // Bug report (2026-11-12, /playlists): the date stays optional and starts EMPTY —
  // prefilling today meant every setlist carried a gig date the user never chose
  // ("make the date field optional"). Create sends it only when it has a value.
  const [gigDate, setGigDate] = useState('');
  const [creating, setCreating] = useState(false);
  const [confirmId, setConfirmId] = useState<string | null>(null);
  const pushToast = useToastStore((s) => s.push);
  const navigate = useNavigate(); // ST-64: intra-app nav without a full reload

  // ST-106: ?tag=<name> shows only the setlists carrying that tag (the tag
  // browser and song/playlist tag chips deep-link here).
  const [params, setParams] = useSearchParams();
  const tag = params.get('tag');

  // ST-64: run token (race) — the newest search wins; stale responses are dropped.
  const loadRun = useRef(0);
  const load = useCallback(async () => {
    const run = ++loadRun.current;
    try {
      const pls = await apiPlaylists(undefined, search.trim() || undefined, tag || undefined);
      if (run !== loadRun.current) return;
      setPlaylists(pls);
      setError(null);
    } catch (e) {
      if (run !== loadRun.current) return;
      // MT-04: offline → the cached playlists, same search + tag semantics.
      if (isNetworkError(e)) {
        const cached = await offlinePlaylists(search.trim() || undefined, tag || undefined);
        if (run !== loadRun.current) return;
        setPlaylists(cached);
        setError(null);
        return;
      }
      setError(e instanceof Error ? e.message : 'Could not load playlists.');
    }
  }, [search, tag]);

  // ST-64: debounce (300 ms) — the load depended on `search` with no debounce,
  // firing a request per keystroke. ST-80: the shared hook (Playlists/Songs
  // duplicated this inline timer).
  useDebouncedEffect(() => void load(), [load]);

  async function create(e: React.FormEvent) {
    e.preventDefault();
    if (!name.trim()) return;
    setCreating(true);
    try {
      const pl = await apiPlaylistCreate({
        name: name.trim(),
        // Bug report (2026-11-12): the setlist toggle is gone — every playlist is a
        // setlist, so creation always sends the flag (the field stays for the API).
        isSetlist: true,
        gigDate: gigDate || undefined,
      });
      pushToast('Playlist created.');
      setName('');
      // Bug report (2026-11-12): the date resets to EMPTY — an optional field starts
      // blank, not with a date the user never picked.
      setGigDate('');
      await load();
      navigate(`/playlists/${pl.id}`);
    } catch (e) {
      pushToast(e instanceof Error ? e.message : 'Create failed.', 'error');
    } finally {
      setCreating(false);
    }
  }

  async function remove(id: string) {
    setConfirmId(null);
    try {
      await apiPlaylistDelete(id);
      pushToast('Playlist deleted.');
      await load();
    } catch (e) {
      pushToast(e instanceof Error ? e.message : 'Delete failed.', 'error');
    }
  }

  // ST-106: the server applied the tag filter (tag= on the list endpoint).
  const shown = playlists;

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

  return (
    <div className={styles.page}>
      {/* ST-86: "is anyone playing right now?" — live sessions on this server,
          each with a Join button. Renders nothing when the network is quiet. */}
      <LiveSessions />

      <form className={styles.createRow} onSubmit={create}>
        <input
          className={styles.input}
          value={name}
          onChange={(e) => setName(e.target.value)}
          placeholder="New playlist name…"
          aria-label="New playlist name"
        />
        {/* Bug report #99: a gig date makes the playlist surface on the dashboard as it nears.
            Bug report (2026-11-12): the field starts empty — optional means empty until you pick. */}
        <label className={styles.dateField}>
          <input
            className={styles.input}
            type="date"
            value={gigDate}
            onChange={(e) => setGigDate(e.target.value)}
            aria-label="Gig date (optional)"
            title="Gig date (optional)"
          />
        </label>
        <button
          type="submit"
          className={`${ui.btn} ${ui.primary}`}
          disabled={creating || !name.trim()}
        >
          {creating ? 'Creating…' : 'Create'}
        </button>
      </form>

      <input
        className={styles.input}
        value={search}
        onChange={(e) => setSearch(e.target.value)}
        placeholder="Filter playlists…"
        aria-label="Filter playlists"
        type="search"
      />

      {tag && (
        <div className={styles.collectionChip}>
          <span>
            Tag: <strong>#{tag}</strong>
          </span>
          <button
            type="button"
            className={`${ui.btn} ${ui.small}`}
            onClick={() =>
              // ST-75 review: updater form — the params object is read-only.
              setParams(
                (prev) => {
                  const next = new URLSearchParams(prev);
                  next.delete('tag');
                  return next;
                },
                { replace: true },
              )
            }
          >
            Show all
          </button>
        </div>
      )}

      {shown === null ? (
        <div className={ui.skeleton} style={{ height: 200 }} />
      ) : shown.length === 0 ? (
        <div className={ui.card}>
          <p className={ui.muted}>
            {tag
              ? 'No setlists carry this tag yet — add it from a setlist page.'
              : search
                ? 'No playlists match your filter.'
                : 'No playlists yet — create your first one above.'}
          </p>
        </div>
      ) : (
        <ul className={styles.list}>
          {shown.map((pl) => (
            // Bug report #120: the whole row opens the playlist (Songs.tsx pattern) —
            // the rowActions cluster stops propagation so RowMenu/badges never navigate;
            // the inner Link stays as the keyboard/tab stop (a11y parity with Songs).
            // The click skips navigation when it bubbles from the Link itself (one push, not two).
            <li
              key={pl.id}
              className={styles.row}
              onClick={(e) => {
                if ((e.target as HTMLElement).closest('a')) return;
                navigate(`/playlists/${pl.id}`);
              }}
            >
              <Link to={`/playlists/${pl.id}`} className={styles.rowLink}>
                <span className={styles.name}>{pl.name}</span>
                <span className={ui.muted}>
                  {pl.songCount} song{pl.songCount === 1 ? '' : 's'}
                </span>
                {/* ST-106: tag chips on the row — click filters the list to that tag. */}
                {!!pl.tags?.length && (
                  <span className={styles.rowTags}>
                    {pl.tags.map((t) => (
                      <span key={t} className={ui.badge}>
                        #{t}
                      </span>
                    ))}
                  </span>
                )}
              </Link>
              {/* Bug report #120: row actions (gig-date badge, ⋯ menu)
                  must not trigger the row navigation. */}
              <span className={styles.rowActions} onClick={(e) => e.stopPropagation()}>
                {/* Bug report #99: gig date rides the row ("15 Nov" / "Gig in 3 days"). */}
                {pl.gigDate && (
                  <span
                    className={`${ui.badge} ${ui.badgeAccent}`}
                    title={`Gig date: ${pl.gigDate}`}
                  >
                    {gigLabel(pl.gigDate)}
                  </span>
                )}
                {/* ST-75: labeled ⋯ delete (was a bare "Delete" text button). */}
                <RowMenu
                  label={`Actions for ${pl.name}`}
                  items={[
                    {
                      key: 'delete',
                      label: 'Delete playlist',
                      icon: <TrashIcon size={16} />,
                      danger: true,
                      onSelect: () => setConfirmId(pl.id),
                    },
                  ]}
                />
              </span>
            </li>
          ))}
        </ul>
      )}

      {confirmId && (
        <div
          className={styles.overlay}
          role="dialog"
          aria-modal="true"
          aria-label="Delete playlist"
        >
          <div className={styles.dialog}>
            <h2>Delete this playlist?</h2>
            <p className={ui.muted}>The songs stay in the library; only the playlist is removed.</p>
            <div className={styles.dialogActions}>
              <button type="button" className={ui.btn} onClick={() => setConfirmId(null)}>
                Cancel
              </button>
              <button
                type="button"
                className={`${ui.btn} ${ui.danger}`}
                onClick={() => void remove(confirmId)}
              >
                Delete
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
