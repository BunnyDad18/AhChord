/**
 * ST-77 (bug report #110): song-list scroll memory. Returning to /songs (e.g. from
 * the player) must restore the saved scroll offset (ahchord.songs.scroll, ST-51 #15).
 * Reproduces: save-on-unmount writes y, remount restores it via window.scrollTo.
 */
import { createRoot } from 'react-dom/client';
import { act } from 'react-dom/test-utils';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
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
let container: HTMLDivElement | null = null;
let root: ReturnType<typeof createRoot> | null = null;

async function mountSongs() {
  (globalThis as Record<string, unknown>).IS_REACT_ACT_ENVIRONMENT = true;
  container = document.createElement('div');
  document.body.appendChild(container);
  root = createRoot(container);
  await act(async () => {
    root!.render(
      <MemoryRouter initialEntries={['/songs']}>
        <Routes>
          <Route path="/songs" element={<Songs />} />
        </Routes>
      </MemoryRouter>,
    );
  });
  await act(async () => {
    await Promise.resolve();
  });
}

beforeEach(() => {
  window.sessionStorage.clear();
});

afterEach(() => {
  act(() => root?.unmount());
  container?.remove();
  root = null;
  container = null;
  vi.restoreAllMocks();
});

describe('songs scroll restore (report #110)', () => {
  it('captures the scroll offset + filter key at SPA navigation (pushState)', async () => {
    // Report (2026-11-13): the save must be SYNCHRONOUS at navigation — the browser's
    // own scroll-to-0 for the new history entry fires as a normal scroll event and
    // raced the old listener+unmount capture to a stored 0.
    await mountSongs();
    Object.defineProperty(window, 'scrollY', { value: 765, configurable: true });
    history.pushState({}, '', '/playlists'); // what a Link click does
    const saved = JSON.parse(window.sessionStorage.getItem(SCROLL_KEY) ?? '{}');
    expect(saved.y).toBe(765);
    expect(saved.key).toBe('|||0||title');
  });

  it('the post-navigation scroll-to-0 cannot clobber the saved position', async () => {
    await mountSongs();
    Object.defineProperty(window, 'scrollY', { value: 765, configurable: true });
    history.pushState({}, '', '/playlists');
    Object.defineProperty(window, 'scrollY', { value: 0, configurable: true });
    window.dispatchEvent(new Event('scroll')); // the browser's navigation snap to top
    act(() => root?.unmount());
    root = null;
    const saved = JSON.parse(window.sessionStorage.getItem(SCROLL_KEY) ?? '{}');
    expect(saved.y).toBe(765);
  });

  it('restores the saved scroll offset on remount', async () => {
    window.sessionStorage.setItem(
      SCROLL_KEY,
      JSON.stringify({ y: 765, page: 1, key: '|||0||title' }),
    );
    const scrollTo = vi.spyOn(window, 'scrollTo').mockImplementation(() => {});
    await mountSongs();
    expect(scrollTo).toHaveBeenCalledWith({ top: 765 });
  });

  it('the post-navigation scroll-to-0 cannot clobber the saved position', async () => {
    // Report (2026-11-13): the browser scrolls the window to 0 for the new history entry
    // (a normal scroll event, list still mounted) and that 0 used to be stored over the
    // deep position. With the synchronous save there is nothing left to clobber.
    await mountSongs();
    Object.defineProperty(window, 'scrollY', { value: 765, configurable: true });
    history.pushState({}, '', '/playlists');
    Object.defineProperty(window, 'scrollY', { value: 0, configurable: true });
    window.dispatchEvent(new Event('scroll'));
    act(() => root?.unmount());
    root = null;
    const saved = JSON.parse(window.sessionStorage.getItem(SCROLL_KEY) ?? '{}');
    expect(saved.y).toBe(765);
  });
});
