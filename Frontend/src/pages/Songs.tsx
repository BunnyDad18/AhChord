import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { Link, useLocation, useNavigate, useSearchParams } from 'react-router-dom';
import {
  apiAcceptAllProposals,
  apiAcceptProposal,
  apiAcceptProposalCategory,
  apiAcceptProposalField,
  apiCollections,
  apiPendingProposalGroups,
  apiPlaylists,
  apiPlaylistAddSong,
  apiMetadataScanProgress,
  apiRejectProposal,
  apiScanMetadata,
  apiSong,
  apiSongDelete,
  apiSongFacets,
  apiSongGroups,
  apiSongProposals,
} from '../api/client';
import type {
  Collection,
  MetadataProposal,
  MetadataScanProgress,
  Playlist,
  ProposalGroup,
  Song,
  SongFacets,
  SongGroup,
} from '../api/types';
import { isNetworkError, offlineAware } from '../offline/fallback';
import {
  offlineCollections,
  offlinePlaylists,
  offlineSongDetail,
  offlineSongFacets,
  offlineSongGroups,
} from '../offline/query';
import { useToastStore } from '../stores/toast';
import { useDebounce } from '../hooks/useDebounce';
import { CopyIcon, EditIcon, PlusIcon, SearchIcon, TrashIcon } from '../components/Icons';
import { RowMenu } from '../components/RowMenu';
import ui from '../components/ui.module.css';
import styles from './Songs.module.css';

const PAGE_SIZE = 50; // API caps pageSize at 100; 50 keeps the list snappy.

/** ST-106: multi-artist row label — first two spelled out, the rest as "+N". */
function artistLabel(g: SongGroup): string {
  const a = g.latest.artists?.length ? g.latest.artists : g.artist ? [g.artist] : [];
  if (a.length <= 2) return a.join(', ');
  return `${a.slice(0, 2).join(', ')} +${a.length - 2}`;
}

/** ST-68: m:ss for the scan strip (elapsed / remaining). */
function fmtDuration(totalSeconds: number): string {
  const s = Math.max(0, Math.round(totalSeconds));
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`;
}

// ST-51 #15: where the list was when the user left — scroll offset + how many "Load more"
// pages were open — so returning to /songs restores the same view instead of snapping back
// to the top with everything collapsed. sessionStorage: per-tab, cleared when the tab closes.
const SCROLL_KEY = 'ahchord.songs.scroll';

interface SongsScrollState {
  y: number; // window scroll offset when leaving
  page: number; // pages open (1 = first page only)
  key: string; // filter signature the state belongs to — stale on mismatch
}

function readSongsScroll(): SongsScrollState | null {
  try {
    const raw = sessionStorage.getItem(SCROLL_KEY);
    if (!raw) return null;
    const s = JSON.parse(raw) as SongsScrollState;
    return typeof s.y === 'number' && typeof s.page === 'number' && typeof s.key === 'string'
      ? s
      : null;
  } catch {
    return null;
  }
}

function writeSongsScroll(s: SongsScrollState) {
  try {
    sessionStorage.setItem(SCROLL_KEY, JSON.stringify(s));
  } catch {
    // storage unavailable — position restore is a nicety, not critical.
  }
}

// ST-35 F3: human-readable labels for proposal fields.
const FIELD_LABELS: Record<string, string> = {
  title: 'Title',
  artist: 'Artist',
  // ST-53: album is reviewable by the metadata job — label it like the other fields.
  album: 'Album',
  genre: 'Genre',
  year: 'Year',
  key: 'Key',
  tempo: 'Tempo',
};

/**
 * Songs library (ST-34; ST-35 D1 rows clickable; ST-35 F1 one row per song): searchable list
 * of every song, grouped by title+artist — a version count badge shows when a song has more
 * than one imported version. The whole row is clickable and plays the LATEST version; Edit,
 * + Playlist… (add to any playlist) and Delete stay as explicit per-row actions (they act on
 * the latest version). Paginated "Load more" over GET /api/songs?grouped=true.
 */
export default function Songs() {
  const navigate = useNavigate();
  const pushToast = useToastStore((s) => s.push);

  // ST-35 F1: one row per song (title+artist), versions collapsed server-side.
  const [groups, setGroups] = useState<SongGroup[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [searchInput, setSearchInput] = useState('');

  // ST-70d: the phone bottom-nav "Search" tab deep-links here with focus intent.
  const location = useLocation();
  const searchRef = useRef<HTMLInputElement>(null);
  useEffect(() => {
    if ((location.state as { focusSearch?: boolean } | null)?.focusSearch) {
      searchRef.current?.focus();
    }
  }, [location.state]);
  const [query, setQuery] = useState(''); // debounced search term
  const [page, setPage] = useState(1);
  const [hasMore, setHasMore] = useState(false);
  const [loadingMore, setLoadingMore] = useState(false);

  // ST-35 D4/D5: filter chips — artist + genre + decade. '' / 0 mean "all".
  // ST-106: tags is the free-tag filter (AND-narrowing, the collection-style filter).
  const [artist, setArtist] = useState('');
  const [genre, setGenre] = useState('');
  const [decade, setDecade] = useState(0);
  const [tags, setTags] = useState<string[]>([]);

  // ST-106: deep links — /songs?artist=|?genre=|?tag= (repeatable) set the filters
  // (song-view artist/genre/tag links land here). ?collection=<id> (the retired
  // collections) resolves to the same-name tag filter once the list is loaded.
  const [searchParams, setSearchParams] = useSearchParams();
  useEffect(() => {
    const a = searchParams.get('artist');
    const g = searchParams.get('genre');
    const t = searchParams
      .getAll('tag')
      .flatMap((s) => s.split(','))
      .map((s) => s.trim())
      .filter(Boolean);
    if (a !== null) setArtist(a);
    if (g !== null) setGenre(g);
    if (t.length) setTags(t);
  }, [searchParams]);
  const [facets, setFacets] = useState<SongFacets>({
    artists: [],
    genres: [],
    decades: [],
    total: 0,
  });
  // ST-75: sort the loaded rows (the API pages in server order; "Sort" orders
  // what is loaded, and "Load more" keeps appending pages).
  const [sort, setSort] = useState('title');

  // ST-51 #15: list-position restore (see SCROLL_KEY). filterKey scopes the saved state to
  // the exact search+filter combination it was captured under. ST-75: the sort is part of
  // the key — a y-offset only identifies a row within the ordering it was captured in.
  const filterKey = `${query}|${artist}|${genre}|${decade}|${tags.join('+')}|${sort}`;
  const savedRef = useRef<SongsScrollState | null>(readSongsScroll());
  const pendingScrollRef = useRef<number | null>(null);

  // Playlists + collections for the add-to-playlist dropdown.
  const [playlists, setPlaylists] = useState<Playlist[]>([]);
  const [collections, setCollections] = useState<Collection[]>([]);

  // ST-106: ?collection=<id> deep links (retired collections) resolve to the
  // same-name tag filter — the backfill made a tag for every collection.
  useEffect(() => {
    const collectionId = searchParams.get('collection');
    if (!collectionId || collections.length === 0) return;
    const col = collections.find((c) => c.id === collectionId);
    if (col) setTags((prev) => (prev.includes(col.name) ? prev : [...prev, col.name]));
    const next = new URLSearchParams(searchParams);
    next.delete('collection');
    setSearchParams(next, { replace: true });
  }, [searchParams, collections, setSearchParams]);

  // Delete confirmation (ST-35 D3: plain confirm/cancel — no typed title).
  const [deleteTarget, setDeleteTarget] = useState<Song | null>(null);
  const [deleting, setDeleting] = useState(false);

  // ST-35 F3: metadata proposal review — inline panel under the row of the song being reviewed.
  const [reviewId, setReviewId] = useState<string | null>(null);

  // ST-68: live metadata-scan progress. The strip shows while running; watching a running
  // scan reach its end (while the page is open) toasts the outcome and refreshes the list.
  const [scan, setScan] = useState<MetadataScanProgress | null>(null);
  const scanWasRunning = useRef(false);
  const [proposals, setProposals] = useState<MetadataProposal[] | null>(null);
  const [proposalBusy, setProposalBusy] = useState<string | null>(null);
  // ST-69: library-wide review — modal listing every pending proposal grouped by category.
  // (Name avoids `groups`, taken by the song-group list state.)
  const [reviewOpen, setReviewOpen] = useState(false);
  const [proposalGroups, setProposalGroups] = useState<ProposalGroup[] | null>(null);
  const [categoryBusy, setCategoryBusy] = useState<string | null>(null);

  // Debounce the search input (300 ms). ST-80: shared hook (the inline
  // window.setTimeout effect here duplicated PlaylistDetail/SearchBox).
  const debouncedInput = useDebounce(searchInput);
  useEffect(() => setQuery(debouncedInput.trim()), [debouncedInput]);

  // ST-51 #15: the open page count + filter signature of the view (see SCROLL_KEY).
  const liveRef = useRef({ page: 1, key: '' });
  useEffect(() => {
    liveRef.current = { page, key: filterKey };
  }, [page, filterKey]);

  // ST-51 #15: persist WHERE the list was (scroll offset + open pages + filter signature).
  // Report (2026-11-13, "goes back to the top of the list"): the scroll-listener +
  // unmount-write capture loses an unwinnable race — on SPA navigation the browser
  // scrolls the window to 0 for the new history entry (a real scroll event, fired while
  // the list is still mounted and tall, so it looks legitimate), and the unmount write
  // then stored that 0 over the deep position. jsdom never fires it, so the DOM tests
  // passed for months. Capture SYNCHRONOUSLY at navigation instead (react-router's own
  // save-on-push pattern): every Link click runs history.pushState while the window is
  // still at the list position. beforeunload covers refresh/close.
  useEffect(() => {
    const save = () =>
      writeSongsScroll({ y: window.scrollY, page: liveRef.current.page, key: liveRef.current.key });
    const origPush = history.pushState.bind(history);
    history.pushState = (...args: Parameters<typeof origPush>) => {
      save();
      return origPush(...args);
    };
    window.addEventListener('beforeunload', save);
    return () => {
      history.pushState = origPush;
      window.removeEventListener('beforeunload', save);
    };
  }, []);

  // ST-51 #15: once the restored rows are in the DOM, put the viewport back where it was.
  useLayoutEffect(() => {
    let timer: number | undefined;
    if (pendingScrollRef.current !== null && groups) {
      const y = pendingScrollRef.current;
      pendingScrollRef.current = null;
      window.scrollTo({ top: y });
      // Rows land first; the Load-more row and the async strips (metadata scan state
      // polls in on a ~5 s cadence) keep growing the page — scrollTo clamps short
      // (958 px measured in the browser, report 2026-11-13). Re-apply until the target
      // is reached, bounded at ~6 s so a genuinely short page (deleted library)
      // settles. Timers, not rAF: background tabs freeze rAF, and this must keep
      // working for a player dock behind a modal, a backgrounded tablet, a preview.
      const deadline = Date.now() + 6000;
      timer = window.setInterval(() => {
        if (window.scrollY >= y - 2 || Date.now() > deadline) {
          window.clearInterval(timer);
          return;
        }
        window.scrollTo({ top: y });
      }, 100);
    }
    return () => {
      if (timer !== undefined) window.clearInterval(timer);
    };
  }, [groups]);

  // ST-64: run token — rapid filter/search changes fire overlapping loads; an older load
  // landing last used to replace the list for a *different* filter and reset page/hasMore
  // inconsistently. Only the newest run may write state (its awaits resolve in order).
  const loadSeq = useRef(0);
  const load = useCallback(async () => {
    const run = ++loadSeq.current;
    try {
      // ST-35 D4: pass the active filters; refresh facets alongside (cheap).
      const [list, pls, cols, f] = await Promise.all([
        apiSongGroups(
          query || undefined,
          1,
          PAGE_SIZE,
          artist || undefined,
          genre || undefined,
          decade || undefined,
          tags.length ? tags : undefined,
        ),
        apiPlaylists(),
        apiCollections(),
        apiSongFacets(),
      ]);
      if (run !== loadSeq.current) return;
      setGroups(list);
      setPage(1);
      setHasMore(list.length === PAGE_SIZE);
      setPlaylists(pls);
      setCollections(cols);
      setFacets(f);
      setError(null);

      // ST-51 #15: if we left this exact filtered view with more pages open, reopen them in
      // parallel and restore the scroll position once the rows have rendered.
      const saved = savedRef.current;
      savedRef.current = null;
      if (saved && saved.key === filterKey) {
        let restoredPage = 1;
        if (saved.page > 1) {
          const targets: number[] = [];
          for (let p = 2; p <= Math.min(saved.page, 20); p++) targets.push(p); // cap ~1000 rows
          const pages = await Promise.all(
            targets.map((p) =>
              apiSongGroups(
                query || undefined,
                p,
                PAGE_SIZE,
                artist || undefined,
                genre || undefined,
                decade || undefined,
                tags.length ? tags : undefined,
              ),
            ),
          );
          if (run !== loadSeq.current) return; // a newer filter change superseded this restore
          setGroups((prev) => (prev ? [...prev, ...pages.flat()] : prev));
          restoredPage = targets[targets.length - 1];
          const last = pages[pages.length - 1];
          setHasMore(last.length === PAGE_SIZE);
        }
        setPage(restoredPage);
        if (saved.y > 0) pendingScrollRef.current = saved.y;
      }
    } catch (e) {
      if (run !== loadSeq.current) return; // superseded — the newer run owns the UI now
      // MT-04: offline → the same list built from the cached library.
      if (isNetworkError(e)) {
        const [list, pls, cols, f] = await Promise.all([
          offlineSongGroups(
            query || undefined,
            1,
            PAGE_SIZE,
            artist || undefined,
            genre || undefined,
            decade || undefined,
            tags.length ? tags : undefined,
          ),
          offlinePlaylists(),
          offlineCollections(),
          offlineSongFacets(),
        ]);
        if (run !== loadSeq.current) return;
        setGroups(list);
        setPage(1);
        setHasMore(list.length === PAGE_SIZE);
        setPlaylists(pls);
        setCollections(cols);
        setFacets(f);
        setError(null);
        return;
      }
      setError(e instanceof Error ? e.message : 'Could not load songs.');
    }
  }, [query, artist, genre, decade, tags, filterKey]);

  // ST-68: poll the scan progress while the page is open (4 s — the strip's clock needs
  // better than minute granularity, the API cost is a single-row read).
  useEffect(() => {
    let alive = true;
    const tick = () => {
      apiMetadataScanProgress()
        .then((p) => {
          if (!alive) return;
          if (scanWasRunning.current && !p.running) {
            const n = p.proposalsAdded ?? 0;
            pushToast(
              p.status === 'interrupted'
                ? 'Metadata scan was interrupted (server restart?) — start it again to resume.'
                : n > 0
                  ? `Metadata scan finished — ${n} proposal${n === 1 ? '' : 's'} created.`
                  : 'Metadata scan finished — no new proposals.',
            );
            void load(); // pick up proposals the scan created
          }
          scanWasRunning.current = p.running;
          setScan(p.running ? p : null);
        })
        .catch(() => {
          /* the strip is chrome — quiet on failure, the next tick retries */
        });
    };
    tick();
    const t = window.setInterval(tick, 4000);
    return () => {
      alive = false;
      window.clearInterval(t);
    };
  }, [load, pushToast]);

  useEffect(() => {
    void load();
  }, [load]);

  async function loadMore() {
    setLoadingMore(true);
    try {
      // MT-04: offline → the next page sliced out of the cached library.
      const next = await offlineAware(
        () =>
          apiSongGroups(
            query || undefined,
            page + 1,
            PAGE_SIZE,
            artist || undefined,
            genre || undefined,
            decade || undefined,
            tags.length ? tags : undefined,
          ),
        () =>
          offlineSongGroups(
            query || undefined,
            page + 1,
            PAGE_SIZE,
            artist || undefined,
            genre || undefined,
            decade || undefined,
            tags.length ? tags : undefined,
          ),
      );
      setGroups((prev) => (prev ? [...prev, ...next] : next));
      setPage(page + 1);
      setHasMore(next.length === PAGE_SIZE);
    } catch (e) {
      pushToast(e instanceof Error ? e.message : 'Could not load more songs.', 'error');
    } finally {
      setLoadingMore(false);
    }
  }

  const collectionName = useMemo(() => {
    const m = new Map<string, string>();
    for (const c of collections) m.set(c.id, c.name);
    return m;
  }, [collections]);

  // ST-75: client-side ordering of the loaded rows (title, artist, newest
  // version, key). The server's page order is untouched — this is a view order.
  const sortedGroups = useMemo(() => {
    const list = groups ? [...groups] : null;
    if (!list) return null;
    switch (sort) {
      case 'artist':
        list.sort(
          (a, b) =>
            (a.latest.artists?.[0] ?? a.artist ?? '~').localeCompare(
              b.latest.artists?.[0] ?? b.artist ?? '~',
            ) || a.title.localeCompare(b.title),
        );
        break;
      case 'recent':
        list.sort(
          (a, b) => new Date(b.latest.createdAt).getTime() - new Date(a.latest.createdAt).getTime(),
        );
        break;
      case 'key':
        list.sort(
          (a, b) =>
            (a.latest.key ?? '~').localeCompare(b.latest.key ?? '~') ||
            a.title.localeCompare(b.title),
        );
        break;
      default:
        list.sort(
          (a, b) =>
            a.title.localeCompare(b.title) || (a.artist ?? '').localeCompare(b.artist ?? ''),
        );
        break;
    }
    return list;
  }, [groups, sort]);

  // ST-75: copy a song's ChordPro text to the clipboard (row ⋯ menu). MT-04:
  // the full chart text is in the local cache, so this works offline too.
  async function copyChordPro(song: Song) {
    try {
      const detail = await offlineAware(
        () => apiSong(song.id),
        () => offlineSongDetail(song.id, 0),
      );
      await navigator.clipboard.writeText(detail.song.chordProText);
      pushToast(`ChordPro for “${song.title}” copied.`);
    } catch (e) {
      pushToast(e instanceof Error ? e.message : 'Could not copy the ChordPro text.', 'error');
    }
  }

  // ST-39 C1: no search term and every filter on "all" → the list is the whole library.
  const unfiltered =
    query === '' && artist === '' && genre === '' && decade === 0 && tags.length === 0;

  async function addToPlaylist(song: Song, playlistId: string) {
    try {
      await apiPlaylistAddSong(playlistId, { songId: song.id });
      const pl = playlists.find((p) => p.id === playlistId);
      pushToast(`Added to ${pl?.name ?? 'playlist'}.`);
    } catch (e) {
      // 409 already-member → the server says so; surface its message.
      pushToast(e instanceof Error ? e.message : 'Could not add the song.', 'error');
    }
  }

  async function doDelete() {
    if (!deleteTarget || deleting) return;
    setDeleting(true);
    try {
      await apiSongDelete(deleteTarget.id);
      pushToast('Version deleted.');
      // ST-35 F1: a delete can change a group's count/latest — refetch to stay correct.
      setDeleteTarget(null);
      void load();
    } catch (e) {
      pushToast(e instanceof Error ? e.message : 'Delete failed.', 'error');
    } finally {
      setDeleting(false);
    }
  }

  // ST-35 F3: open/close the inline proposal panel for a song's latest version.
  async function toggleReview(songId: string) {
    if (reviewId === songId) {
      setReviewId(null);
      setProposals(null);
      return;
    }
    setReviewId(songId);
    setProposals(null);
    try {
      setProposals(await apiSongProposals(songId));
    } catch (e) {
      pushToast(e instanceof Error ? e.message : 'Could not load the proposals.', 'error');
      setReviewId(null);
    }
  }

  // ST-35 F3: accept/reject one proposal, then refresh the list (counts + badges).
  async function resolveProposal(p: MetadataProposal, accept: boolean) {
    if (!reviewId || proposalBusy) return;
    setProposalBusy(p.id);
    try {
      if (accept) await apiAcceptProposal(reviewId, p.id);
      else await apiRejectProposal(reviewId, p.id);
      const rest = (proposals ?? []).filter((x) => x.id !== p.id);
      setProposals(rest);
      pushToast(accept ? 'Proposal applied.' : 'Proposal rejected.');
      if (rest.length === 0) {
        setReviewId(null);
        setProposals(null);
      }
      void load();
    } catch (e) {
      pushToast(e instanceof Error ? e.message : 'Could not update the proposal.', 'error');
    } finally {
      setProposalBusy(null);
    }
  }

  // ST-67: mass-approve — accept every pending proposal for this song. Proposals that can no
  // longer be applied stay pending, so the panel is refetched from the server.
  async function acceptAllProposals() {
    if (!reviewId || proposalBusy) return;
    setProposalBusy('all');
    try {
      const { applied, skipped } = await apiAcceptAllProposals(reviewId);
      pushToast(
        applied === 0
          ? 'Nothing to apply — none of these proposals can be applied any more.'
          : `Applied ${applied} proposal${applied === 1 ? '' : 's'}.${skipped > 0 ? ` ${skipped} left pending.` : ''}`,
      );
      const rest = await apiSongProposals(reviewId);
      setProposals(rest);
      if (rest.length === 0) {
        setReviewId(null);
        setProposals(null);
      }
      void load();
    } catch (e) {
      pushToast(e instanceof Error ? e.message : 'Could not apply the proposals.', 'error');
    } finally {
      setProposalBusy(null);
    }
  }

  // ST-35 F3: kick off a full-library metadata scan (background job).
  async function scanMetadata() {
    try {
      await apiScanMetadata();
      // ST-68: the progress strip (below the header) now shows live status — no more silence.
      pushToast('Metadata scan started — progress shows below the header.');
    } catch (e) {
      pushToast(e instanceof Error ? e.message : 'Could not start the scan.', 'error');
    }
  }

  // ST-69: open the library-wide review panel — pending proposals grouped by field + value.
  async function openProposalGroups() {
    setReviewOpen(true);
    setProposalGroups(null);
    try {
      setProposalGroups(await apiPendingProposalGroups());
    } catch (e) {
      pushToast(e instanceof Error ? e.message : 'Could not load the proposals.', 'error');
      setReviewOpen(false);
    }
  }

  // ST-69: mass-approve one category (field + value across the whole library), like ST-67's
  // per-song pass — proposals that can no longer be applied stay pending, so groups refetch.
  async function acceptCategory(g: ProposalGroup) {
    if (categoryBusy) return;
    const key = `${g.field}\u0000${g.value}`;
    setCategoryBusy(key);
    try {
      const { applied, skipped, songs } = await apiAcceptProposalCategory(g.field, g.value);
      pushToast(
        applied === 0
          ? 'Nothing to apply — none of these proposals can be applied any more.'
          : `Applied ${applied} proposal${applied === 1 ? '' : 's'} across ${songs} song${songs === 1 ? '' : 's'}.${
              skipped > 0 ? ` ${skipped} left pending.` : ''
            }`,
      );
      const rest = await apiPendingProposalGroups();
      setProposalGroups(rest);
      void load();
    } catch (e) {
      pushToast(e instanceof Error ? e.message : 'Could not apply the proposals.', 'error');
    } finally {
      setCategoryBusy(null);
    }
  }

  // ST-71: mass-approve a whole field — every pending proposal for it, across all values
  // ("accept all year changes"). The busy key gets a "*" suffix so it can't collide with a
  // category key (a field named "*"? values are the LLM's — fields are the fixed set).
  async function acceptField(field: string) {
    if (categoryBusy) return;
    const key = `${field}\u0000*`;
    setCategoryBusy(key);
    try {
      const { applied, skipped, songs } = await apiAcceptProposalField(field);
      pushToast(
        applied === 0
          ? 'Nothing to apply — none of these proposals can be applied any more.'
          : `Applied ${applied} ${field} proposal${applied === 1 ? '' : 's'} across ${songs} song${songs === 1 ? '' : 's'}.${
              skipped > 0 ? ` ${skipped} left pending.` : ''
            }`,
      );
      const rest = await apiPendingProposalGroups();
      setProposalGroups(rest);
      void load();
    } catch (e) {
      pushToast(e instanceof Error ? e.message : 'Could not apply the proposals.', 'error');
    } finally {
      setCategoryBusy(null);
    }
  }

  // ST-71: section the ST-69 categories by field so a field spread over many values
  // (31 distinct years, 18 keys…) gets ONE accept-all for the whole field, not 31 clicks.
  // Field order = first appearance, i.e. the field owning the biggest category leads.
  const fieldSections = useMemo(() => {
    if (!proposalGroups) return null;
    const byField = new Map<string, ProposalGroup[]>();
    for (const g of proposalGroups) {
      const list = byField.get(g.field) ?? [];
      list.push(g);
      byField.set(g.field, list);
    }
    return [...byField.entries()].map(([field, groups]) => ({
      field,
      groups,
      total: groups.reduce((n, g) => n + g.count, 0),
      songs: new Set(groups.flatMap((g) => g.songs.map((s) => s.songId))).size,
    }));
  }, [proposalGroups]);

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
      {/* ST-68: live metadata-scan progress — current song, n/total, elapsed, coarse ETA. */}
      {scan?.running && (
        <div className={styles.scanStrip} role="status" aria-live="polite">
          <strong>Scanning metadata</strong>
          {scan.current ? (
            <span className={styles.scanCurrent}>{scan.current}</span>
          ) : (
            <span className={ui.muted}>starting…</span>
          )}
          <span className={ui.muted}>
            {scan.index ?? 0}/{scan.total ?? 0}
          </span>
          <span className={ui.muted}>{fmtDuration(scan.elapsedSeconds ?? 0)} elapsed</span>
          {scan.etaSeconds != null && (
            <span className={ui.muted}>~{fmtDuration(scan.etaSeconds)} left</span>
          )}
          {(scan.proposalsAdded ?? 0) > 0 && (
            <span className={ui.muted}>
              {scan.proposalsAdded} proposal{(scan.proposalsAdded ?? 0) === 1 ? '' : 's'}
            </span>
          )}
        </div>
      )}

      {/* ST-75: one toolbar — search + filter chips + sort (the sticky filter
          card and its three bare selects are gone). */}
      <div className={styles.toolbar}>
        {/* Bug report #111: "Manage library" lives next to the search bar now, on the
            toolbar's first row (it sat in the page head, above the search). */}
        <div className={styles.searchRow}>
          <input
            ref={searchRef}
            className={styles.input}
            value={searchInput}
            onChange={(e) => setSearchInput(e.target.value)}
            placeholder="Search title or artist…"
            aria-label="Search songs"
            type="search"
          />
          {/* Bug report #88: the page-level actions fold into one labeled "Manage
              library" menu. "+ New song" is gone — the Import page covers creation
              (paste → review → save), and this page keeps the song list calm. */}
          <RowMenu
            label="Manage library"
            triggerLabel="Manage library"
            align="left"
            items={[
              // ST-35 F3: ask the LLM to fill in missing metadata across the library.
              {
                key: 'scan',
                label: 'Scan metadata',
                icon: <SearchIcon size={16} />,
                onSelect: () => void scanMetadata(),
              },
              // ST-69: library-wide proposal review — categories, one click each.
              {
                key: 'proposals',
                label: 'Review proposals',
                onSelect: () => void openProposalGroups(),
              },
            ]}
          />
        </div>
        <div className={styles.filterRow}>
          <label className={styles.chipSelect}>
            <span>Artist</span>
            <select
              value={artist}
              onChange={(e) => setArtist(e.target.value)}
              aria-label="Filter by artist"
            >
              <option value="">All</option>
              {facets.artists.map((a) => (
                <option key={a} value={a}>
                  {a}
                </option>
              ))}
            </select>
          </label>
          <label className={styles.chipSelect}>
            <span>Genre</span>
            <select
              value={genre}
              onChange={(e) => setGenre(e.target.value)}
              aria-label="Filter by genre"
            >
              <option value="">All</option>
              {facets.genres.map((g) => (
                <option key={g} value={g}>
                  {g}
                </option>
              ))}
            </select>
          </label>
          <label className={styles.chipSelect}>
            <span>Decade</span>
            <select
              value={decade}
              onChange={(e) => setDecade(Number(e.target.value))}
              aria-label="Filter by decade"
            >
              <option value={0}>Any</option>
              {facets.decades.map((d) => (
                <option key={d} value={d}>
                  {d}s
                </option>
              ))}
            </select>
          </label>
          {/* Bug report #85: `artist || genre || decade` evaluates to the number 0 when
              all are empty, and React renders it — the stray "0" between the chips. */}
          {(artist || genre || decade > 0 || tags.length > 0) && (
            <button
              type="button"
              className={`${ui.btn} ${ui.small}`}
              onClick={() => {
                setArtist('');
                setGenre('');
                setDecade(0);
                setTags([]);
              }}
            >
              Clear filters
            </button>
          )}
          <label className={`${styles.chipSelect} ${styles.sortChip}`}>
            <span>Sort</span>
            <select value={sort} onChange={(e) => setSort(e.target.value)} aria-label="Sort songs">
              <option value="title">Title</option>
              <option value="artist">Artist</option>
              <option value="recent">Recently added</option>
              <option value="key">Key</option>
            </select>
          </label>
        </div>
        {/* ST-106: free-tag chips — the collection-style filter, click to add/remove
            (several tags AND-narrow: "Sunday" + "Stage" = songs with both). */}
        {!!facets.tags?.length && (
          <div className={styles.tagRow} role="group" aria-label="Filter by tag">
            {facets.tags.map((t) => {
              const on = tags.includes(t);
              return (
                <button
                  key={t}
                  type="button"
                  className={`${ui.badge} ${on ? styles.tagActive : ''}`}
                  aria-pressed={on}
                  onClick={() => setTags(on ? tags.filter((x) => x !== t) : [...tags, t])}
                >
                  #{t}
                </button>
              );
            })}
          </div>
        )}
      </div>

      <div className={styles.content}>
        <div className={styles.listCol}>
          {sortedGroups === null ? (
            <div className={ui.skeleton} style={{ height: 240 }} />
          ) : sortedGroups.length === 0 ? (
            <div className={ui.card}>
              {query || artist || genre || decade || tags.length ? (
                <p className={ui.muted}>No songs match the current search/filters.</p>
              ) : (
                <p className={ui.muted}>
                  No songs yet. <Link to="/import">Import one</Link> or{' '}
                  <Link to="/songs/new">create a new song</Link>.
                </p>
              )}
            </div>
          ) : (
            <>
              <ul className={styles.list}>
                {sortedGroups.map((g) => (
                  // ST-35 D1/F1: the whole row plays the LATEST version; actions stop propagation.
                  // ST-77 review: skip the click when it bubbles from the inner Link (one push, not two).
                  <li
                    key={g.latest.id}
                    className={styles.row}
                    onClick={(e) => {
                      if ((e.target as HTMLElement).closest('a')) return;
                      navigate(`/songs/${g.latest.id}/player`);
                    }}
                  >
                    {/* ST-37 C3: title over artist; badges stay on the title line. */}
                    <Link to={`/songs/${g.latest.id}/player`} className={styles.songInfo}>
                      <span className={styles.songTitleLine}>
                        <strong>{g.title}</strong>
                        {/* ST-35 F1: version count badge (only when the song has >1 imported version). */}
                        {g.versionCount > 1 && (
                          <span
                            className={`${ui.badge} ${styles.countBadge}`}
                            title={`${g.versionCount} versions — see them on the song page`}
                          >
                            ×{g.versionCount}
                          </span>
                        )}
                        {g.latest.key && (
                          <span className={`${ui.badge} ${styles.keyBadge}`}>{g.latest.key}</span>
                        )}
                        {g.latest.capoFret > 0 && (
                          <span className={ui.badge}>Capo {g.latest.capoFret}</span>
                        )}
                      </span>
                      {artistLabel(g) && <span className={ui.muted}>{artistLabel(g)}</span>}
                    </Link>
                    {/* ST-75: one grammar — ▶ plays, + adds, ⋯ holds View/Edit/Copy/Delete
                    (destructive actions get a label, never a bare red icon). */}
                    <span className={styles.rowActions} onClick={(e) => e.stopPropagation()}>
                      {/* ST-35 F3: pending metadata proposals → inline review panel (amber chip). */}
                      {g.pendingProposals > 0 && (
                        <button
                          type="button"
                          className={`${ui.btn} ${ui.small} ${styles.reviewChip}`}
                          onClick={() => void toggleReview(g.latest.id)}
                          title="Pending metadata proposals"
                        >
                          Review ({g.pendingProposals})
                        </button>
                      )}
                      {/* Bug report #90: no play button — the whole row opens the song. */}
                      {/* ST-37 C1: compact add-to-playlist — icon button + menu (was a wide select). */}
                      <PlaylistAddButton
                        songTitle={g.title}
                        playlists={playlists}
                        collectionName={collectionName}
                        onAdd={(playlistId) => void addToPlaylist(g.latest, playlistId)}
                      />
                      <RowMenu
                        label={`Actions for ${g.title}`}
                        items={[
                          {
                            key: 'view',
                            label: 'Open song page',
                            onSelect: () => navigate(`/songs/${g.latest.id}`),
                          },
                          {
                            key: 'edit',
                            label: 'Edit',
                            icon: <EditIcon size={16} />,
                            onSelect: () => navigate(`/songs/${g.latest.id}/edit`),
                          },
                          {
                            key: 'copy',
                            label: 'Copy ChordPro (.cho)',
                            icon: <CopyIcon size={16} />,
                            onSelect: () => void copyChordPro(g.latest),
                          },
                          {
                            key: 'delete',
                            label: g.versionCount > 1 ? 'Delete this version' : 'Delete',
                            icon: <TrashIcon size={16} />,
                            danger: true,
                            onSelect: () => setDeleteTarget(g.latest),
                          },
                        ]}
                      />
                    </span>

                    {/* ST-35 F3: inline proposal review (full-width, wraps under the row).
                    ST-36 A2: stop propagation — Accept/Review clicks must not open the player. */}
                    {reviewId === g.latest.id && (
                      <div className={styles.proposalPanel} onClick={(e) => e.stopPropagation()}>
                        <div className={styles.proposalHead}>
                          <h3 className={styles.proposalTitle}>Proposed metadata</h3>
                          {/* ST-67: mass-approve — one click for the whole panel. */}
                          {proposals !== null && proposals.length > 1 && (
                            <button
                              type="button"
                              className={`${ui.btn} ${ui.small}`}
                              disabled={proposalBusy !== null}
                              onClick={() => void acceptAllProposals()}
                            >
                              Accept all ({proposals.length})
                            </button>
                          )}
                        </div>
                        {proposals === null ? (
                          <p className={ui.muted}>Loading…</p>
                        ) : proposals.length === 0 ? (
                          <p className={ui.muted}>No pending proposals.</p>
                        ) : (
                          proposals.map((p) => (
                            <div key={p.id} className={styles.proposalRow}>
                              <span className={styles.proposalField}>
                                {FIELD_LABELS[p.field] ?? p.field}
                              </span>
                              <span className={ui.muted}>{p.currentValue || '—'}</span>
                              <span aria-hidden="true">→</span>
                              <strong>{p.proposedValue}</strong>
                              <button
                                type="button"
                                className={`${ui.btn} ${ui.small}`}
                                disabled={proposalBusy !== null}
                                onClick={() => void resolveProposal(p, true)}
                              >
                                Accept
                              </button>
                              <button
                                type="button"
                                className={`${ui.btn} ${ui.small}`}
                                disabled={proposalBusy !== null}
                                onClick={() => void resolveProposal(p, false)}
                              >
                                Reject
                              </button>
                            </div>
                          ))
                        )}
                      </div>
                    )}
                  </li>
                ))}
              </ul>
              {/* ST-39 C1 / bug report #85: the plain number by the filters read as a
              stray "0" — the load button now just says "Load more", and the total
              line states what the number means (hidden for an empty library). */}
              {hasMore ? (
                <div className={styles.loadMore}>
                  <button
                    type="button"
                    className={ui.btn}
                    disabled={loadingMore}
                    onClick={() => void loadMore()}
                  >
                    {loadingMore
                      ? 'Loading…'
                      : unfiltered
                        ? 'Load more'
                        : `Load more (${sortedGroups?.length ?? 0} shown)`}
                  </button>
                </div>
              ) : groups !== null && unfiltered && facets.total > 0 ? (
                <p className={styles.listTotal}>{facets.total} songs in the library</p>
              ) : null}
            </>
          )}
        </div>
      </div>

      {/* ST-69: library-wide proposal review — one category (field → value) per row,
          each with a mass-approve; the song list is expandable before approving. */}
      {reviewOpen && (
        <div
          className={styles.overlay}
          role="dialog"
          aria-modal="true"
          aria-label="Review proposals"
          onClick={() => setReviewOpen(false)}
        >
          <div className={styles.reviewDialog} onClick={(e) => e.stopPropagation()}>
            <div className={styles.reviewHead}>
              <h2>Pending proposals</h2>
              <button
                type="button"
                className={`${ui.btn} ${ui.small}`}
                onClick={() => setReviewOpen(false)}
              >
                Close
              </button>
            </div>
            {proposalGroups === null ? (
              <p className={ui.muted}>Loading…</p>
            ) : proposalGroups.length === 0 ? (
              <p className={ui.muted}>
                No pending proposals anywhere — the library is all caught up.
              </p>
            ) : (
              <>
                <p className={ui.muted}>
                  {proposalGroups.reduce((n, g) => n + g.count, 0)} proposals in{' '}
                  {proposalGroups.length} categor{proposalGroups.length === 1 ? 'y' : 'ies'}.
                  Accepting a category applies it to every song it matches; accepting a whole field
                  applies every proposal for that field at once.
                </p>
                <div className={styles.reviewBody}>
                  {(fieldSections ?? []).map((sec) => (
                    <div key={sec.field} className={styles.fieldSection}>
                      {/* ST-71: field-level accept — only when the field has more than one
                          category (a single-value field is already one click via its category). */}
                      {sec.groups.length > 1 && (
                        <div className={styles.fieldHead}>
                          <span className={styles.proposalField}>
                            {FIELD_LABELS[sec.field] ?? sec.field}
                          </span>
                          <span className={ui.muted}>
                            whole field — {sec.total} proposal{sec.total === 1 ? '' : 's'} in{' '}
                            {sec.groups.length} categor{sec.groups.length === 1 ? 'y' : 'ies'},{' '}
                            {sec.songs} song{sec.songs === 1 ? '' : 's'}
                          </span>
                          <button
                            type="button"
                            className={`${ui.btn} ${ui.small} ${ui.primary}`}
                            disabled={!!categoryBusy}
                            onClick={() => void acceptField(sec.field)}
                          >
                            {categoryBusy === `${sec.field}\u0000*`
                              ? 'Applying…'
                              : `Accept all (${sec.total})`}
                          </button>
                        </div>
                      )}
                      <ul className={styles.groupList}>
                        {sec.groups.map((g) => {
                          const key = `${g.field}\u0000${g.value}`;
                          return (
                            <li key={key} className={styles.groupItem}>
                              <div className={styles.groupHead}>
                                <span className={styles.proposalField}>
                                  {FIELD_LABELS[g.field] ?? g.field}
                                </span>
                                → <span className={styles.groupValue}>“{g.value}”</span>
                                <span className={ui.muted}>
                                  {g.count} proposal{g.count === 1 ? '' : 's'}, {g.songs.length}{' '}
                                  song
                                  {g.songs.length === 1 ? '' : 's'}
                                </span>
                                <button
                                  type="button"
                                  className={`${ui.btn} ${ui.small} ${ui.primary}`}
                                  disabled={!!categoryBusy}
                                  onClick={() => void acceptCategory(g)}
                                >
                                  {categoryBusy === key ? 'Applying…' : `Accept all (${g.count})`}
                                </button>
                              </div>
                              <details className={styles.groupSongs}>
                                <summary>Which songs?</summary>
                                <ul>
                                  {g.songs.map((s) => (
                                    <li key={s.songId}>
                                      <Link
                                        to={`/songs/${s.songId}`}
                                        onClick={() => setReviewOpen(false)}
                                      >
                                        {s.title}
                                      </Link>
                                      {s.artist ? ` — ${s.artist}` : ''}
                                      {s.currentValue
                                        ? ` (now: ${s.currentValue})`
                                        : ' (currently empty)'}
                                    </li>
                                  ))}
                                </ul>
                              </details>
                            </li>
                          );
                        })}
                      </ul>
                    </div>
                  ))}
                </div>
              </>
            )}
          </div>
        </div>
      )}

      {deleteTarget && (
        <div className={styles.overlay} role="dialog" aria-modal="true" aria-label="Delete song">
          <div className={styles.dialog}>
            <h2>Delete “{deleteTarget.title}”?</h2>
            {/* ST-35 D3: plain confirm/cancel — no typed-title requirement. */}
            <p className={ui.muted}>This removes the song from the library and every playlist.</p>
            <div className={styles.dialogActions}>
              <button type="button" className={ui.btn} onClick={() => setDeleteTarget(null)}>
                Cancel
              </button>
              <button
                type="button"
                className={`${ui.btn} ${ui.danger}`}
                disabled={deleting}
                onClick={() => void doDelete()}
              >
                {deleting ? 'Deleting…' : 'Delete song'}
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}

/**
 * ST-37 C1: compact add-to-playlist — a small icon button that opens a menu of playlists
 * (grouped by collection name), replacing the wide "+ Playlist…" select in row cards.
 */
function PlaylistAddButton({
  songTitle,
  playlists,
  collectionName,
  onAdd,
}: {
  songTitle: string;
  playlists: Playlist[];
  collectionName: Map<string, string>;
  onAdd: (playlistId: string) => void;
}) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  // Close when clicking anywhere outside the button + menu.
  useEffect(() => {
    if (!open) return;
    function onDoc(e: MouseEvent) {
      if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false);
    }
    document.addEventListener('mousedown', onDoc);
    return () => document.removeEventListener('mousedown', onDoc);
  }, [open]);

  return (
    <div className={styles.plMenu} ref={ref}>
      <button
        type="button"
        className={`${ui.btn} ${ui.small} ${ui.iconOnly} ${playlists.length === 0 ? styles.plAddOff : ''}`}
        aria-label={`Add ${songTitle} to a playlist`}
        title={
          playlists.length === 0
            ? 'No playlists yet — make one on the Playlists page'
            : 'Add to playlist'
        }
        disabled={playlists.length === 0}
        onClick={() => setOpen((v) => !v)}
      >
        <PlusIcon size={18} />
      </button>
      {open && (
        <div
          className={styles.plMenuList}
          role="menu"
          aria-label={`Add ${songTitle} to a playlist`}
        >
          {playlists.map((pl) => {
            const col = pl.collectionId ? collectionName.get(pl.collectionId) : undefined;
            return (
              <button
                key={pl.id}
                type="button"
                role="menuitem"
                className={styles.plMenuItem}
                onClick={() => {
                  setOpen(false);
                  onAdd(pl.id);
                }}
              >
                {col ? `${col} / ${pl.name}` : pl.name}
              </button>
            );
          })}
        </div>
      )}
    </div>
  );
}
