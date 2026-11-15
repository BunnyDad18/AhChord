/**
 * Bug report (2026-11-12, /songs): "when you return from a song back to the songs
 * page the position in the list is reset". The ST-77 test covered the plain
 * unmount→remount; this covers the REPORTED route: /songs → player → history Back.
 * The app owns scroll (history.scrollRestoration = 'manual' in main.tsx) and the
 * page restores the saved offset on remount.
 */
import { createRoot } from 'react-dom/client';
import { act } from 'react-dom/test-utils';
import { MemoryRouter, Route, Routes, useLocation, useNavigate } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { SongGroup } from '../api/types';

vi.mock('../api/client', () => ({
  apiAcceptAllProposals: vi.fn(),
  apiAcceptProposal: vi.fn(),
  apiAcceptProposalCategory: vi.fn(),
  apiAcceptProposalField: vi.fn(),
  apiCollections: vi.fn(() => Promise.resolve([])),
  apiPendingProposalGroups: vi.fn(() => Promise.resolve([])),
  apiPlaylists: vi.fn(() => Promise.resolve([])),
  apiPlaylistAddSong: vi.fn(),
  apiMetadataScanProgress: vi.fn(() => Promise.resolve(null)),
  apiRejectProposal: vi.fn(),
  apiScanMetadata: vi.fn(),
  apiSong: vi.fn(),
  apiSongDelete: vi.fn(),
  apiSongFacets: vi.fn(() => Promise.resolve({ artists: [], genres: [], decades: [], total: 0 })),
  apiSongGroups: vi.fn(() =>
    Promise.resolve(
      Array.from({ length: 25 }, (_, i) => ({
        title: `Song ${i}`,
        artist: null,
        versionCount: 1,
        latest: { id: `s${i}`, title: `Song ${i}`, artist: null, createdAt: '', updatedAt: '' },
      })) as unknown as SongGroup[],
    ),
  ),
  apiSongProposals: vi.fn(() => Promise.resolve([])),
}));

import Songs from './Songs';

const SCROLL_KEY = 'ahchord.songs.scroll';

function StubPlayer() {
  return <div>player</div>;
}

let container: HTMLDivElement | null = null;
let root: ReturnType<typeof createRoot> | null = null;

// A location sink: route changes (including history Back) render through it,
// so the test can see which route is live after each act(). The navigate ref is
// captured from inside the router (useNavigate) for the history-Back step.
// (Ref-suffixed: writing .current is a ref write, not a value mutation.)
const seen: string[] = [];
const navigateSinkRef: { current: ((delta: number) => void) | null } = { current: null };

function Sink() {
  const location = useLocation();
  seen.push(location.pathname);
  const nav = useNavigate();
  navigateSinkRef.current = nav;
  return null;
}

async function mountShell() {
  (globalThis as Record<string, unknown>).IS_REACT_ACT_ENVIRONMENT = true;
  container = document.createElement('div');
  document.body.appendChild(container);
  root = createRoot(container);
  await act(async () => {
    root!.render(
      <MemoryRouter initialEntries={['/songs']}>
        <Sink />
        <Routes>
          <Route path="/songs" element={<Songs />} />
          <Route path="/songs/:id/player" element={<StubPlayer />} />
        </Routes>
      </MemoryRouter>,
    );
  });
  await act(async () => {
    await Promise.resolve();
  });
}

function setScrollY(y: number) {
  Object.defineProperty(window, 'scrollY', { value: y, configurable: true });
  window.dispatchEvent(new Event('scroll'));
}

beforeEach(() => {
  window.sessionStorage.clear();
  seen.length = 0;
  navigateSinkRef.current = null;
});

afterEach(() => {
  act(() => root?.unmount());
  container?.remove();
  root = null;
  container = null;
  vi.restoreAllMocks();
});

describe('songs list position survives the trip to the player and Back (report 2026-11-12)', () => {
  it('saves on leave, restores the offset on history Back, and never scrolls on first open', async () => {
    const scrollTo = vi.spyOn(window, 'scrollTo').mockImplementation(() => {});

    await mountShell();
    // First open at the top: the restore branch must not fire a scroll.
    expect(scrollTo).not.toHaveBeenCalled();

    // Scroll deep into the list, open a song (the row opens the player), press Back.
    setScrollY(765);
    const link = container!.querySelector<HTMLAnchorElement>('a[href="/songs/s1/player"]');
    if (!link) throw new Error('row link not rendered');
    // The app router (BrowserRouter) writes window.history DURING the click (the page's
    // save rides it — report 2026-11-13); MemoryRouter does not, so model the push first.
    history.pushState({}, '', '/songs/s1/player');
    await act(async () => {
      link.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, button: 0 }));
    });
    expect(seen.at(-1)).toBe('/songs/s1/player');

    // History Back (the router's own history — the app's back affordance).
    await act(async () => {
      navigateSinkRef.current!(-1);
      await Promise.resolve();
    });
    expect(seen.at(-1)).toBe('/songs');

    // The saved state carried the offset, and the remount scrolled back to it.
    const saved = JSON.parse(window.sessionStorage.getItem(SCROLL_KEY) ?? '{}');
    expect(saved.y).toBe(765);
    expect(scrollTo).toHaveBeenCalledWith({ top: 765 });
  });
});
