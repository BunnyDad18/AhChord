import { useCallback, useEffect, useRef, useState } from 'react';
import type { CSSProperties, UIEvent } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { apiPlaylist, apiSong, apiSongRandom, apiSongUpdate, apiSongView } from '../api/client';
import { ApiError } from '../api/types';
import type { PlaylistDetail, Song, SongDetailResponse } from '../api/types';
import { offlineAware } from '../offline/fallback';
import {
  offlinePlaylist,
  offlineRandomSong,
  offlineSongDetail,
  recordView,
} from '../offline/query';
import { OfflineIndicator } from '../components/OfflineBanner';
import { useToastStore } from '../stores/toast';
import { useSessionActivityStore } from '../stores/sessionActivity';
import { useTransposeStore } from '../stores/transpose';
import { BugReport } from '../components/BugReport';
import { FullscreenToggle } from '../components/FullscreenToggle';
import { ChevronIcon, EditIcon, ShuffleIcon, UsersIcon, XIcon } from '../components/Icons';
import { KeyCapoControl } from '../components/KeyCapoControl';
import { effectiveKey } from '../components/TransposeControl';
import { ChordName } from '../components/ChordName';
import { ColumnsControl } from '../components/ColumnsControl';
import { SongRenderer } from '../components/SongRenderer';
import { recordLastPlay } from '../utils/lastPlay';
import { useColumnPref } from '../utils/columns';
import { joinSession, type SessionHandle } from '../realtime/sessionClient';
import { publishFollow } from '../realtime/followClient';
import ui from '../components/ui.module.css';
import styles from './Player.module.css';

/** Remember the last position per playlist within the session (ST-19). */
const lastPosition = new Map<string, number>();

/** ST-84: the overlay dock (and page arrows) fades after this idle time. The
 *  hints line never fades; the dock only WAKES when the cursor gets close to it. */
const IDLE_MS = 4000;

/** ST-84 polish (James: "disappear faster"): once the cursor LEAVES the dock's
 *  area the bar is gone in this short time — the full IDLE_MS only applies
 *  while the cursor idles near the bar. */
const IDLE_LEAVE_MS = 600;

/** …and the first timer on page load is short too — the bar greets, then bows. */
const IDLE_START_MS = 2000;

/** ST-84 polish: how far from the dock's edge the cursor must be to wake it. */
const WAKE_NEAR_PX = 96;

type Mode = 'setlist' | 'single';

interface PlayerProps {
  mode: Mode;
}

/**
 * Fullscreen player (ST-19 + ST-25). Setlist mode steps through a playlist and
 * syncs with other participants over the SignalR session hub (next/prev/capo
 * broadcast, participant count, reconnect banner). Single-song mode stays
 * local: prev/next = random library songs. Keyboard: →/↓ next, ←/↑ prev,
 * Esc back, +/- capo, R random.
 */
export default function Player({ mode }: PlayerProps) {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  // ST-39 E1: transpose (-12…+12) replaces the old 0–6 capo.
  const transpose = useTransposeStore((s) => s.transpose);
  const bumpTranspose = useTransposeStore((s) => s.bump);
  const pushToast = useToastStore((s) => s.push);

  // ST-42: column layout preference (auto | 1 | 2 | 3), persisted per browser, shared with the song page.
  const [columns, setColumns] = useColumnPref();

  // ST-75: lyrics text size (A−/A+ on the dock) — same localStorage key as the
  // song page, so one setting governs the band's reading size everywhere.
  const [fontScale, setFontScale] = useState(() => {
    const v = Number(window.localStorage.getItem('ahchord.songFontScale'));
    return Number.isFinite(v) && v >= 0.8 && v <= 1.6 ? v : 1;
  });

  function bumpFont(delta: number) {
    setFontScale((s) => Math.max(0.8, Math.min(1.6, Math.round((s + delta) * 10) / 10)));
  }

  // ST-75 review: persist in an effect — state updaters run twice under
  // StrictMode; the side effect belongs here, not inside the updater.
  useEffect(() => {
    window.localStorage.setItem('ahchord.songFontScale', String(fontScale));
  }, [fontScale]);

  // Bug report (2026-11-12): "a button to turn off the chords for someone who is
  // only singing" — display-only (AHC-2): the chart text never changes, the chords
  // simply don't render. Persisted per device, like the text size.
  const [showChords, setShowChords] = useState(
    () => window.localStorage.getItem('ahchord.showChords') !== '0',
  );
  useEffect(() => {
    window.localStorage.setItem('ahchord.showChords', showChords ? '1' : '0');
  }, [showChords]);

  const [playlist, setPlaylist] = useState<PlaylistDetail | null>(null);
  const [index, setIndex] = useState(0);
  const [songData, setSongData] = useState<SongDetailResponse | null>(null);
  const [loadedSongId, setLoadedSongId] = useState<string | null>(null);
  // ST-77 #122: committed capo changes bump this — the projection shift is
  // (transpose − capo), so the chart refetches with the new shift.
  const [reloadKey, setReloadKey] = useState(0);
  const [randomSong, setRandomSong] = useState<Song | null>(null);
  const [error, setError] = useState<string | null>(null);
  const scrollRef = useRef<HTMLDivElement>(null);
  // ST-83/84: the dock and page arrows are an OVERLAY that auto-hides — the dock
  // WAKES when the cursor gets CLOSE to it (ST-84 polish: a mouse anywhere in the
  // window no longer reveals it), ~4s of idle fades it out (hover/keyboard focus
  // inside the dock pauses the timer), and phones also hide it on scroll-down.
  // The hints line rides OUTSIDE the fade — it is always visible.
  const [chromeHidden, setChromeHidden] = useState(false);
  const lastScrollTop = useRef(0);
  const idleTimer = useRef<number | null>(null);
  const dockRef = useRef<HTMLDivElement>(null);

  const resetIdle = useCallback((ms = IDLE_MS) => {
    if (idleTimer.current !== null) window.clearTimeout(idleTimer.current);
    idleTimer.current = window.setTimeout(() => setChromeHidden(true), ms);
  }, []);

  const wake = useCallback(
    (ms = IDLE_MS) => {
      quickFired.current = false;
      setChromeHidden((h) => (h ? false : h));
      resetIdle(ms);
    },
    [resetIdle],
  );

  // ST-84 polish: once the cursor is OUT of the dock's area, one short hide
  // deadline is set (600ms) — further pointer moves far away do not extend it,
  // so the bar bows out promptly while the reader keeps moving the mouse.
  const quickFired = useRef(false);
  const hideSoon = useCallback(() => {
    if (quickFired.current) return;
    quickFired.current = true;
    resetIdle(IDLE_LEAVE_MS);
  }, [resetIdle]);

  const pauseIdle = useCallback(() => {
    if (idleTimer.current !== null) {
      window.clearTimeout(idleTimer.current);
      idleTimer.current = null;
    }
  }, []);

  // ST-84 polish: the dock wakes on cursor PROXIMITY (within WAKE_NEAR_PX of its
  // edge) — a mouse moving mid-window keeps it hidden — and when the cursor
  // LEAVES that area the bar fades fast (IDLE_LEAVE_MS, not 4s). Key press and
  // phone touch wake with the full idle window. Events INSIDE the dock never
  // re-arm the timer (an open popover must not fade under the user). Focus:
  // focus INTO the dock shows the controls and pauses the fade; focus landing
  // outside it (arrow click, chart controls) does NOT wake — the pointer is not
  // near the dock, so per James the bar stays hidden (Tab users: the Tab key
  // press itself woke it). Focus leaving the dock re-arms the timer.
  useEffect(() => {
    const inDock = (t: EventTarget | null) => dockRef.current?.contains(t as Node);
    const nearDock = (x: number, y: number) => {
      const r = dockRef.current?.getBoundingClientRect();
      if (!r) return false;
      return (
        x >= r.left - WAKE_NEAR_PX &&
        x <= r.right + WAKE_NEAR_PX &&
        y >= r.top - WAKE_NEAR_PX &&
        y <= r.bottom + WAKE_NEAR_PX
      );
    };
    const onPointerMove = (e: PointerEvent) => {
      if (inDock(e.target)) return; // hover-pause handles events inside
      if (nearDock(e.clientX, e.clientY)) wake();
      else hideSoon(); // cursor is out of the area — bow out quickly (once)
    };
    const onWakeKey = (e: Event) => {
      if (!inDock(e.target)) wake();
    };
    const onFocusIn = (e: FocusEvent) => {
      if (inDock(e.target)) {
        setChromeHidden(false);
        pauseIdle();
      }
    };
    const onFocusOut = (e: FocusEvent) => {
      // Focus left the dock (e.g. Esc closed the popover → focus to body): the
      // pause must end, or the chrome never auto-hides again.
      if (!inDock(e.relatedTarget)) wake(IDLE_LEAVE_MS);
    };
    window.addEventListener('pointermove', onPointerMove, { passive: true });
    window.addEventListener('keydown', onWakeKey, { passive: true });
    window.addEventListener('touchstart', onWakeKey, { passive: true });
    window.addEventListener('focusin', onFocusIn);
    window.addEventListener('focusout', onFocusOut);
    wake(IDLE_START_MS); // greet on load, then bow quickly
    return () => {
      window.removeEventListener('pointermove', onPointerMove);
      window.removeEventListener('keydown', onWakeKey);
      window.removeEventListener('touchstart', onWakeKey);
      window.removeEventListener('focusin', onFocusIn);
      window.removeEventListener('focusout', onFocusOut);
      if (idleTimer.current !== null) window.clearTimeout(idleTimer.current);
      idleTimer.current = null;
    };
  }, [wake, pauseIdle, hideSoon]);

  const onChartScroll = useCallback(
    (e: UIEvent<HTMLDivElement>) => {
      const y = e.currentTarget.scrollTop;
      wake(); // any scroll re-arms the idle timer; the phone hide below wins by order
      if (y < 80) setChromeHidden(false);
      else if (y - lastScrollTop.current > 16) setChromeHidden(true);
      else if (lastScrollTop.current - y > 16) setChromeHidden(false);
      lastScrollTop.current = y;
    },
    [wake],
  );

  // ST-84: thin ‹ › arrows at the page edges step through the ST-83 fit-to-height
  // pages when the chart runs wider than the screen (phones keep the vertical
  // model — no flow row, no arrows). Observed, not guessed: the layout plan flips
  // between passes and re-plans on font/column/resize changes.
  const [flowNav, setFlowNav] = useState<{ left: boolean; right: boolean } | null>(null);
  const flowElRef = useRef<HTMLElement | null>(null);

  const syncFlowNav = useCallback(() => {
    const el = scrollRef.current?.querySelector<HTMLElement>('[data-flow]');
    if (!el) {
      setFlowNav((prev) => (prev === null ? prev : null));
      return;
    }
    const left = el.scrollLeft > 4;
    const right = el.scrollLeft < el.scrollWidth - el.clientWidth - 4;
    setFlowNav((prev) =>
      prev && prev.left === left && prev.right === right ? prev : { left, right },
    );
  }, []);

  // ST-84 polish: one step = ONE COLUMN (James: "shows column 1 and 2, scroll
  // right 1 → shows column 2 and 3") — the pages tile edge to edge, so the
  // first page's width is the column pitch.
  const stepFlow = useCallback((dir: 1 | -1) => {
    const el = flowElRef.current;
    if (!el) return;
    const page = el.firstElementChild as HTMLElement | null;
    const step = page?.getBoundingClientRect().width || el.clientWidth;
    el.scrollBy({ left: dir * step, behavior: 'smooth' });
  }, []);

  // ST-84 polish: the mouse wheel pages the fit-to-height flow row side to side
  // (vertical delta counts as horizontal — the row has no vertical scroll).
  // Trackpad inertia accumulates into whole page steps. Only wired on the flow
  // row (the classic vertical model keeps native wheel scrolling); wheel inside
  // the Key & Capo popover is left native.
  const wheelAcc = useRef(0);
  const onFlowWheel = useCallback(
    (e: WheelEvent) => {
      const el = flowElRef.current;
      if (!el || dockRef.current?.contains(e.target as Node)) return;
      let d = Math.abs(e.deltaX) > Math.abs(e.deltaY) ? e.deltaX : e.deltaY;
      if (e.deltaMode === 1)
        d *= 16; // line mode (Firefox mice)
      else if (e.deltaMode === 2) d *= el.clientWidth; // page mode
      if (Math.abs(d) < 4) return;
      e.preventDefault();
      wheelAcc.current += d;
      if (Math.abs(wheelAcc.current) < 50) return;
      stepFlow(wheelAcc.current > 0 ? 1 : -1);
      wheelAcc.current = 0;
    },
    [stepFlow],
  );

  // ST-84: measure the dock — the phone bar wraps to 2–3 rows by width, so the
  // hints line and the Key & Capo popover anchor off the MEASURED height
  // (--dock-h feeds the hints' bottom and the phone popover's bottom in CSS).
  const [dockH, setDockH] = useState(0);
  useEffect(() => {
    const el = dockRef.current;
    if (!el || typeof ResizeObserver === 'undefined') return;
    const ro = new ResizeObserver(() => setDockH(el.offsetHeight));
    ro.observe(el);
    setDockH(el.offsetHeight);
    return () => ro.disconnect();
  }, []);

  // Stable identity so SongRenderer's memo (ST-28) can skip re-renders when
  // session/participants/capo state changes.
  const renderChord = useCallback((name: string) => <ChordName name={name} />, []);

  // ST-25 session state (setlist mode only).
  const [session, setSession] = useState<SessionHandle | null>(null);
  const [participants, setParticipants] = useState(0);
  const [reconnecting, setReconnecting] = useState(false);
  // ST-55: automatic reconnection exhausted -> show a dead-connection banner with Reconnect.
  const [disconnected, setDisconnected] = useState(false);
  const [joinNonce, setJoinNonce] = useState(0);
  const remoteCapoRef = useRef<number | null>(null);

  const isSetlist = mode === 'setlist';
  const currentSongId = isSetlist
    ? playlist?.songs[Math.min(index, (playlist?.songs.length ?? 1) - 1)]?.id
    : (randomSong?.id ?? id);

  // Derived loading: skeleton until the data shown matches the current song.
  // Capo re-fetches keep the previous content visible (no flash).
  const songReady = !!currentSongId && !!songData && loadedSongId === currentSongId;
  const loading = !error && !songReady;

  // ST-84: page-arrow wiring (needs songReady — declared above).
  useEffect(() => {
    if (!songReady) return;
    const host = scrollRef.current;
    if (!host) return;
    // The horizontal scroller lives inside SongRenderer and appears/disappears
    // with the layout plan — track it through the DOM, keep its scroll listener
    // attached, and re-sync on any structural change.
    const attach = () => {
      const el = host.querySelector<HTMLElement>('[data-flow]');
      if (el === flowElRef.current) return;
      if (flowElRef.current) {
        flowElRef.current.removeEventListener('scroll', syncFlowNav);
        flowElRef.current.removeEventListener('wheel', onFlowWheel);
      }
      flowElRef.current = el;
      if (el) {
        el.addEventListener('scroll', syncFlowNav, { passive: true });
        el.addEventListener('wheel', onFlowWheel, { passive: false });
      }
      syncFlowNav();
    };
    attach();
    const mo = new MutationObserver(attach);
    mo.observe(host, { childList: true, subtree: true });
    const ro = typeof ResizeObserver !== 'undefined' ? new ResizeObserver(syncFlowNav) : null;
    ro?.observe(host);
    return () => {
      mo.disconnect();
      ro?.disconnect();
      if (flowElRef.current) {
        flowElRef.current.removeEventListener('scroll', syncFlowNav);
        flowElRef.current.removeEventListener('wheel', onFlowWheel);
      }
      flowElRef.current = null;
    };
  }, [songReady, syncFlowNav, onFlowWheel]);

  // ST-51 #8: the song's stored capo (server is source of truth; display-only, additive).
  const capoFret = songData?.song.capoFret ?? 0;

  // Load the playlist once (setlist mode). MT-04: offline → the cached setlist.
  useEffect(() => {
    if (!isSetlist || !id) return;
    offlineAware(
      () => apiPlaylist(id),
      () => offlinePlaylist(id),
    )
      .then((pl) => {
        setPlaylist(pl);
        // ST-75: the dashboard "Jump back in" hero remembers this setlist.
        recordLastPlay({ type: 'setlist', id: pl.id, title: pl.name });
        const saved = lastPosition.get(pl.id);
        if (saved !== undefined && saved >= 0 && saved < pl.songs.length) {
          setIndex(saved);
        }
      })
      .catch((e: unknown) =>
        setError(e instanceof Error ? e.message : 'Could not load the setlist.'),
      );
  }, [isSetlist, id]);

  // ST-75: single-song plays feed the dashboard "Jump back in" hero too
  // (setlist entries record the playlist itself, above).
  useEffect(() => {
    if (isSetlist || !songReady || !songData || !currentSongId) return;
    recordLastPlay({
      type: 'song',
      id: currentSongId,
      title: songData.song.title,
      artist: songData.song.artist,
    });
  }, [isSetlist, songReady, songData, currentSongId]);

  // ST-87: hosting — the player publishes my session's view. In setlist mode the
  // view is the SETLIST (joined devices open this player; the ST-25 transport keeps
  // the songs in sync from there). A single-song player publishes the song itself.
  useEffect(() => {
    if (!songReady || !songData || !currentSongId) return;
    void publishFollow(
      isSetlist && playlist
        ? {
            kind: 'setlist',
            songId: currentSongId,
            songTitle: songData.song.title,
            songArtist: songData.song.artist,
            playlistId: playlist.id,
            songIndex: index,
          }
        : {
            kind: 'song',
            songId: currentSongId,
            songTitle: songData.song.title,
            songArtist: songData.song.artist,
          },
    );
    // Publishes on each song change (currentSongId) — transpose/capo edits stay
    // out of the follow view; the transport session owns those.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [songReady, currentSongId]);

  // ST-25: join the session once the setlist is known.
  useEffect(() => {
    if (!isSetlist || !playlist) return;
    let handle: SessionHandle | null = null;
    let disposed = false;
    const start = playlist.songs.length > 0 ? playlist.id : null;
    if (!start) return;

    joinSession(start, lastPosition.get(start), {
      onState: (s) => {
        if (disposed) return;
        setIndex(s.songIndex);
        lastPosition.set(start, s.songIndex);
        // ST-39 E1: the session hub still carries a `capo` int — now a -12…+12 transpose.
        // ST-55: transient — in setlist mode the store's songId belongs to an unrelated song,
        // so a remote transpose must not persist there.
        if (s.capo !== useTransposeStore.getState().transpose) {
          remoteCapoRef.current = s.capo;
          useTransposeStore.getState().setTransient(s.capo);
        }
      },
      onParticipants: (p) => {
        if (!disposed) setParticipants(p.count);
      },
      onReconnecting: (r) => {
        if (!disposed) setReconnecting(r);
      },
      onClosed: () => {
        // ST-55: reconnection retries exhausted — stop pretending, surface it.
        if (disposed) return;
        setSession(null);
        setReconnecting(false);
        setDisconnected(true);
        useSessionActivityStore.getState().setActive(false);
      },
    })
      .then((h) => {
        if (disposed) {
          void h.leave();
          return;
        }
        handle = h;
        setSession(h);
        setDisconnected(false);
        // ST-70g: the top-bar activity box ranks a live playing session first.
        useSessionActivityStore.getState().setActive(true);
        // ST-55: no local participants seed — the Join broadcast carries the real count.
      })
      .catch(() => {
        /* session is a nice-to-have; local mode still works */
      });

    return () => {
      disposed = true;
      setSession(null);
      setParticipants(0);
      useSessionActivityStore.getState().setActive(false);
      if (handle) void handle.leave();
    };
  }, [isSetlist, playlist?.id, joinNonce]); // eslint-disable-line react-hooks/exhaustive-deps

  // Broadcast local transpose changes to the session (skip echoes of remote capo).
  useEffect(() => {
    if (!session || !playlist) return;
    if (remoteCapoRef.current === transpose) {
      remoteCapoRef.current = null;
      return;
    }
    void session.setTranspose(transpose).catch(() => {
      // ST-55: the hub accepts the full -12…+12 range, so a rejection now means a real
      // transport problem — say so instead of silently forking the band's keys.
      pushToast('Could not sync the transpose — others see the old key.', 'error');
    });
  }, [transpose, session, playlist, pushToast]);

  // Load the current song (by id + per-song transpose). ST-51 #3: in single mode the store's
  // value belongs to this song — load it before fetching. In setlist mode the session hub is
  // the source of truth for transpose, so we don't touch the store here. The ref dedupes the
  // re-run that setForSong triggers when the loaded value differs from the current one.
  // ST-77 #122: a committed capo change bumps reloadKey — the projection shift is
  // (transpose − capo), so the chart must refetch with the new shift.
  const fetchedRef = useRef<{ id: string; t: number; k: number } | null>(null);
  useEffect(() => {
    if (!currentSongId) return;
    if (!isSetlist) useTransposeStore.getState().setForSong(currentSongId);
    const t = useTransposeStore.getState().transpose;
    if (
      fetchedRef.current?.id === currentSongId &&
      fetchedRef.current.t === t &&
      fetchedRef.current.k === reloadKey
    )
      return;
    fetchedRef.current = { id: currentSongId, t, k: reloadKey };
    let cancelled = false;
    // MT-04: offline → parse + transpose the cached ChordPro (the server's projection, client-side).
    offlineAware(
      () => apiSong(currentSongId, t),
      () => offlineSongDetail(currentSongId, t),
    )
      .then((data) => {
        if (cancelled) return;
        setSongData(data);
        setLoadedSongId(currentSongId);
        setError(null);
      })
      .catch((e: unknown) => {
        if (cancelled) return;
        setSongData(null);
        setError(
          e instanceof ApiError && e.status === 404
            ? 'Song not found.'
            : e instanceof Error
              ? e.message
              : 'Could not load the song.',
        );
      });
    return () => {
      cancelled = true;
      // ST-62: clear the dedupe guard on cleanup — StrictMode's setup→cleanup→setup cycle
      // left setup 2 at the fetchedRef early-return with its only fetch cancelled, so the
      // player hung on a skeleton forever (dev-only).
      fetchedRef.current = null;
    };
  }, [currentSongId, transpose, isSetlist, reloadKey]);

  // View history for the current song (feeds Recent).
  useEffect(() => {
    if (!currentSongId) return;
    recordView(currentSongId); // MT-04: local copy — Recent works offline too
    apiSongView(currentSongId).catch(() => {
      /* best effort */
    });
  }, [currentSongId]);

  // Reset scroll per song.
  useEffect(() => {
    scrollRef.current?.scrollTo({ top: 0 });
    // ST-83: the flow model pages horizontally — a new song lands on its first page.
    scrollRef.current?.querySelector('[data-flow]')?.scrollTo({ left: 0 });
    setChromeHidden(false);
    lastScrollTop.current = 0;
  }, [currentSongId]);

  const total = isSetlist ? (playlist?.songs.length ?? 0) : 0;

  const goRandom = useCallback(async () => {
    if (!isSetlist) {
      try {
        // MT-04: offline → a random song from the cached library.
        setRandomSong(
          await offlineAware(
            () => apiSongRandom(),
            () => offlineRandomSong(),
          ),
        );
      } catch {
        /* stay put */
      }
    } else if (playlist && playlist.songs.length > 0) {
      try {
        const pick = await offlineAware(
          () => apiSongRandom(playlist.id),
          () => offlineRandomSong(playlist.id),
        );
        const i = playlist.songs.findIndex((s) => s.id === pick.id);
        if (i >= 0) {
          setIndex(i);
          lastPosition.set(playlist.id, i);
          // ST-55: the hub moves the session, so every participant follows; local-only
          // when there is no session (degraded mode).
          if (session)
            void session.goTo(i).catch(() => {
              /* stay local */
            });
        }
      } catch {
        /* stay put */
      }
    }
  }, [isSetlist, playlist, session]);

  const next = useCallback(() => {
    if (!isSetlist) {
      void goRandom();
      return;
    }
    if (!playlist || playlist.songs.length === 0) return;
    if (session) {
      // ST-25: broadcast; the returned state is applied via onState.
      void session.next().catch(() => {
        /* stay put */
      });
    } else {
      setIndex((i) => {
        const n = (i + 1) % playlist.songs.length; // wraps at the end
        lastPosition.set(playlist.id, n);
        return n;
      });
    }
  }, [isSetlist, playlist, session, goRandom]);

  const prev = useCallback(() => {
    if (!isSetlist) {
      void goRandom();
      return;
    }
    if (!playlist || playlist.songs.length === 0) return;
    if (session) {
      void session.prev().catch(() => {
        /* stay put */
      });
    } else {
      setIndex((i) => {
        const n = (i - 1 + playlist.songs.length) % playlist.songs.length;
        lastPosition.set(playlist.id, n);
        return n;
      });
    }
  }, [isSetlist, playlist, session, goRandom]);

  const back = useCallback(() => {
    navigate(-1);
  }, [navigate]);

  // ST-51 #8: persist the song's stored capo (0–6) — optimistic update, rollback on failure.
  const commitCapo = useCallback(
    async (fret: number) => {
      if (!currentSongId || !songData) return;
      const prev = songData.song.capoFret;
      setSongData({ ...songData, song: { ...songData.song, capoFret: fret } });
      try {
        await apiSongUpdate(currentSongId, { capoFret: fret });
        // ST-77 #122: the shapes shown = chart shifted by (transpose − capo) — a new capo
        // means a new projection, so refetch the chart.
        setReloadKey((k) => k + 1);
      } catch (e: unknown) {
        setSongData((d) => (d ? { ...d, song: { ...d.song, capoFret: prev } } : d));
        pushToast(e instanceof Error ? e.message : 'Could not save the capo.', 'error');
      }
    },
    [currentSongId, songData, pushToast],
  );

  // Keyboard controls.
  useEffect(() => {
    function onKey(e: KeyboardEvent) {
      const tag = (e.target as HTMLElement | null)?.tagName;
      if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT') return;
      switch (e.key) {
        case 'ArrowRight':
        case 'ArrowDown':
          e.preventDefault();
          next();
          break;
        case 'ArrowLeft':
        case 'ArrowUp':
          e.preventDefault();
          prev();
          break;
        case 'Escape':
          back();
          break;
        case '+':
        case '=':
          bumpTranspose(1, isSetlist);
          break;
        case '-':
        case '_':
          bumpTranspose(-1, isSetlist);
          break;
        case 'r':
        case 'R':
          void goRandom();
          break;
        case 'PageDown':
          // ST-84: step the fit-to-height columns ONE COLUMN (arrows stay on prev/next song).
          stepFlow(1);
          break;
        case 'PageUp':
          stepFlow(-1);
          break;
      }
    }
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [next, prev, back, bumpTranspose, goRandom, isSetlist, stepFlow]);

  const songTitle = songData?.song.title ?? (isSetlist ? '…' : (randomSong?.title ?? '…'));

  // ST-84: the key/capo chip rides the top bar's left slot — a permanent fixture
  // (neutral shows the plain key, bug report #92's "no line when neutral" is retired
  // in favor of a stable bar: setting a capo/transpose moves nothing, ever).
  const soundingKey = effectiveKey(songData?.song.key, transpose);
  const keyChipLabel =
    capoFret > 0
      ? soundingKey
        ? `Capo ${capoFret} · Key of ${soundingKey}`
        : `Capo ${capoFret}`
      : soundingKey
        ? `Key of ${soundingKey}`
        : 'As written';

  return (
    <div
      className={`${styles.player} ${chromeHidden ? styles.chromeHidden : ''}`}
      style={dockH > 0 ? ({ '--dock-h': `${dockH}px` } as CSSProperties) : undefined}
    >
      {reconnecting && (
        <div className={styles.reconnect} role="alert">
          Reconnecting…
        </div>
      )}
      {/* ST-55: reconnection retries exhausted — sync is gone until a manual rejoin. */}
      {disconnected && (
        <div className={styles.reconnect} role="alert">
          <span>Session connection lost — sync is off.</span>
          <button
            type="button"
            className={`${ui.btn} ${ui.small}`}
            onClick={() => {
              setDisconnected(false);
              setJoinNonce((n) => n + 1);
            }}
          >
            Reconnect
          </button>
        </div>
      )}

      {/* ST-36 C2 · ST-84 polish: fixed-height bar — key/capo chip in the left
          slot, title centered, status right (phones: plus an X exit at the far
          right; desktop exits via browser back / Esc). Nothing here is
          conditional, so the bar (and the chart below it) never moves. */}
      <div className={styles.topBar}>
        {/* ST-84: "Capo 3 · Key of A" in a permanently reserved slot (absolutely
            placed — width changes shift nothing). Tune it from Key & Capo in the dock. */}
        <span
          className={styles.keyChip}
          title="Capo and the key you will hear — adjust with Key & Capo below"
        >
          {keyChipLabel}
        </span>

        <div className={styles.songHead}>
          <h1 className={styles.title}>{songTitle}</h1>
          {/* Always rendered — an empty artist keeps the bar at its fixed height
              (review ST-84: '' is falsy, so the nbsp covers null AND empty). */}
          <span
            className={`${ui.muted} ${styles.artist} ${songData?.song.artist ? '' : styles.artistGhost}`}
          >
            {songData?.song.artist || '\u00A0'}
          </span>
        </div>

        {/* ST-75: status chrome only — transport + reading controls live in the
            dock (ST-84 polish: the bug report moved down there too). */}
        <div className={styles.topActions}>
          {/* MT-04: the charts on screen come from the cache while offline. */}
          <OfflineIndicator />
          {isSetlist && participants > 0 && (
            <span className={styles.participants} aria-live="polite">
              {/* ST-36 E1: flat users icon (was emoji). */}
              <UsersIcon size={16} /> {participants} in session
            </span>
          )}
          {/* ST-84 polish: phones get a quiet X exit top-right (the shell has no
              browser chrome); desktop removed the Back button entirely. */}
          <button
            type="button"
            className={styles.closeBtn}
            onClick={back}
            aria-label="Close player"
            title="Close"
          >
            <XIcon size={20} />
          </button>
        </div>
      </div>

      <div
        ref={scrollRef}
        className={styles.scrollArea}
        onScroll={onChartScroll}
        style={{ '--lyric-scale': String(fontScale) } as CSSProperties}
      >
        {loading && (
          <div className={styles.loading}>
            <div className={ui.skeleton} style={{ height: 40, width: '40%' }} />
            <div className={ui.skeleton} style={{ height: 240, marginTop: 24 }} />
          </div>
        )}
        {!loading && error && (
          <div className={ui.errorBanner} role="alert">
            <span>{error}</span>
          </div>
        )}
        {!loading && !error && songData && (
          <SongRenderer
            parsed={songData.parsed}
            renderChord={renderChord}
            multiColumn
            columns={columns}
            fontScale={fontScale}
            layoutEpoch={reconnecting ? 1 : 0}
            flowPages
            showChords={showChords}
          />
        )}
      </div>

      {/* ST-84 polish: column arrows — thin ‹ › on the edges, each present ONLY
          while a move in its direction is possible (no disabled ghosts), and
          ALWAYS visible (they never fade with the auto-hiding bar). One tap =
          one column. */}
      {flowNav?.left && (
        <button
          type="button"
          className={`${styles.edge} ${styles.edgeL}`}
          onClick={() => stepFlow(-1)}
          aria-label="Scroll one column left"
          title="One column left"
        >
          <ChevronIcon dir="left" size={18} />
        </button>
      )}
      {flowNav?.right && (
        <button
          type="button"
          className={`${styles.edge} ${styles.edgeR}`}
          onClick={() => stepFlow(1)}
          aria-label="Scroll one column right"
          title="One column right"
        >
          <ChevronIcon dir="right" size={18} />
        </button>
      )}

      {/* ST-75 / ST-84: the dock — transport (setlist) / Random (single) plus Key &
          Capo, columns and text size, every mode. It floats OVER the chart (ST-84
          polish: no reservation — the song takes the full height, the bar is ok to
          overlap on appear), auto-hides on idle, and only wakes when the cursor
          gets close. Hover/focus inside pauses the fade.
          Keyboard: → next · ← prev · +/− transpose · R random · Esc exit. */}
      <div
        ref={dockRef}
        className={styles.dock}
        role="toolbar"
        aria-label="Player controls"
        onPointerEnter={pauseIdle}
        onPointerLeave={() => wake()}
      >
        {isSetlist ? (
          <div className={styles.transport}>
            <button type="button" className={ui.btn} onClick={prev} aria-label="Previous song">
              ← Prev
            </button>
            <div className={styles.progressWrap}>
              <div
                className={styles.progress}
                role="progressbar"
                aria-valuemin={1}
                aria-valuemax={Math.max(total, 1)}
                aria-valuenow={Math.min(total, index + 1)}
                aria-label={`Song ${Math.min(total, index + 1)} of ${total}`}
              >
                <div
                  className={styles.progressFill}
                  style={{
                    width: total > 0 ? `${((index + 1) / total) * 100}%` : '0%',
                  }}
                />
              </div>
              <span className={styles.position}>{total > 0 ? `${index + 1} / ${total}` : '—'}</span>
              {/* ST-27: screen-reader announcement on song change ("Song N of M: <title>"). */}
              <span className={ui.srOnly} aria-live="polite" aria-atomic="true">
                {total > 0
                  ? `Song ${Math.min(total, index + 1)} of ${total}: ${songData?.song?.title ?? ''}`
                  : ''}
              </span>
            </div>
            {/* ST-35 D6: standard button scale (was an oversized 56px CTA). */}
            <button
              type="button"
              className={`${ui.btn} ${ui.primary}`}
              onClick={next}
              aria-label="Next song"
            >
              Next →
            </button>
          </div>
        ) : (
          <div className={styles.transport}>
            <button
              type="button"
              className={`${ui.btn} ${ui.primary}`}
              onClick={() => void goRandom()}
              aria-label="Play a random song"
            >
              <ShuffleIcon size={18} />{' '}
              {/* ST-84 polish: phones get the icon alone (no label) — the whole bar
                  fits on one row; desktop keeps the word. */}
              <span className={styles.btnLabel}>Random</span>
            </button>
          </div>
        )}

        <div className={styles.dockTools}>
          {/* ST-51 #8 / ST-55: capo is saved with the song; the setlist session owns the transpose. */}
          <KeyCapoControl
            songKey={songData?.song.key}
            capoFret={capoFret}
            onCommitCapo={(fret) => void commitCapo(fret)}
            disabled={!songData}
            persist={!isSetlist}
            placement="up"
          />
          <span className={styles.fontGroup} role="group" aria-label="Lyrics font size">
            <button
              type="button"
              onClick={() => bumpFont(-0.1)}
              aria-label="Decrease lyrics font size"
            >
              A−
            </button>
            <button
              type="button"
              onClick={() => bumpFont(0.1)}
              aria-label="Increase lyrics font size"
            >
              A+
            </button>
          </span>
          {/* Bug report (2026-11-12): chords off for singers — display-only, per device. */}
          <button
            type="button"
            className={styles.chordsGroup}
            aria-pressed={showChords}
            onClick={() => setShowChords((v) => !v)}
            aria-label="Show chords"
            title={
              showChords
                ? 'Chords shown — tap to hide them (just the words)'
                : 'Chords hidden — tap to show them'
            }
          >
            Chords
          </button>
          {/* ST-42: column layout (Auto / 1 / 2 / 3), shared with the song page. */}
          <ColumnsControl value={columns} onChange={setColumns} size="sm" />
          {/* Bug report (2026-11-12): full-screen — the browser bar disappears. */}
          <FullscreenToggle className={styles.fsBtn} />
          {/* ST-36 C1 / ST-39 D1: bug report from the player carries the song as
              context (ST-84 polish: moved from the top bar into the dock). */}
          <BugReport
            context={`player — ${songTitle}${songData?.song.artist ? ` by ${songData.song.artist}` : ''}`}
          />
          {/* ST-51 #1: edit button on the player — same target as the song page's
              Edit (ST-84 polish: icon-only on phones, one-row bar). */}
          {songReady && currentSongId && (
            <Link to={`/songs/${currentSongId}/edit`} className={`${ui.btn} ${ui.small}`}>
              <EditIcon size={16} /> <span className={styles.btnLabel}>Edit</span>
            </Link>
          )}
        </div>
      </div>

      {/* ST-75: the keyboard-hints line — always visible, and it NEVER moves:
          pinned to the bottom-left corner on desktop (the pill floats at the
          centre, over the chart); above the phone bar on phones. */}
      <p className={styles.hints} aria-hidden="true">
        → next · ← prev · +/− transpose · R random · Esc back
      </p>

      {/* ST-84: the wake handle — visible only while the chrome is faded. */}
      <div className={styles.handle} aria-hidden="true" />
    </div>
  );
}
