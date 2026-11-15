/**
 * ST-84: the player top bar carries a permanent "Capo X · Key Y" chip (it replaces
 * the conditional capo line under the title — the bar used to grow and push the
 * chart when a capo/transpose appeared). The chip is always rendered, so there is
 * no layout to shift; this test pins the chip's content in both states, asserts
 * the old "Transpose …" line is gone, and checks the overlay dock + hints exist.
 */
import { createRoot } from 'react-dom/client';
import { act } from 'react-dom/test-utils';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { afterEach, beforeAll, beforeEach, describe, expect, it, vi } from 'vitest';
import type { SongDetailResponse } from '../api/types';

vi.mock('../api/client', () => ({
  apiPlaylist: vi.fn(() => Promise.resolve(null)),
  apiSong: vi.fn(),
  apiSongRandom: vi.fn(() => Promise.resolve(null)),
  apiSongUpdate: vi.fn(() => Promise.resolve(null)),
  apiSongView: vi.fn(() => Promise.resolve(null)),
}));

vi.mock('../offline/fallback', () => ({
  offlineAware: (online: () => Promise<unknown>) => online(),
}));

vi.mock('../offline/query', () => ({
  offlinePlaylist: vi.fn(),
  offlineRandomSong: vi.fn(),
  offlineSongDetail: vi.fn(),
  recordView: vi.fn(),
}));

vi.mock('../realtime/sessionClient', () => ({
  joinSession: vi.fn(() =>
    Promise.resolve({ leave: vi.fn(), next: vi.fn(), prev: vi.fn(), goTo: vi.fn(), setTranspose: vi.fn() }),
  ),
}));

import Player from './Player';
import { useTransposeStore } from '../stores/transpose';

// jsdom has no Element.scroll* — the player's per-song scroll reset calls it.
beforeAll(() => {
  const noop = () => {};
  Element.prototype.scrollTo = noop;
  Element.prototype.scrollBy = noop;
});

function detail(capoFret: number): SongDetailResponse {
  return {
    song: {
      id: 'x1',
      title: 'Shift Test',
      artist: 'Agent',
      key: 'G',
      capoFret,
      createdAt: '2026-11-01T00:00:00Z',
      updatedAt: '2026-11-01T00:00:00Z',
      chordProText: '{title: Shift Test}\n\n{start_of_verse}\nHello [C] world\n{end_of_verse}\n',
    },
    parsed: {
      header: { title: 'Shift Test' },
      sections: [
        {
          type: 'verse',
          label: 'Verse 1',
          lines: [{ text: 'Hello world', chords: [{ name: 'C', charIndex: 6 }], annotations: [], bars: [] }],
        },
      ],
      warnings: [],
    },
  } as unknown as SongDetailResponse;
}

let container: HTMLDivElement | null = null;

async function mountPlayer() {
  (globalThis as Record<string, unknown>).IS_REACT_ACT_ENVIRONMENT = true;
  container = document.createElement('div');
  document.body.appendChild(container);
  const root = createRoot(container);
  await act(async () => {
    root.render(
      <MemoryRouter initialEntries={['/songs/x1/player']}>
        <Routes>
          <Route path="/songs/:id/player" element={<Player mode="single" />} />
        </Routes>
      </MemoryRouter>,
    );
  });
  await act(async () => {
    await Promise.resolve();
    await Promise.resolve();
  });
  return root;
}

beforeEach(() => {
  window.localStorage.clear();
  // The transpose store is a module singleton; test 1's value would ride along.
  useTransposeStore.getState().setTransient(0);
});

afterEach(() => {
  container?.remove();
  container = null;
  vi.clearAllMocks();
});

describe('player key/capo chip (ST-84)', () => {
  it('shows "Capo 3 · Key of A" with capo 3 and transpose +2 — and no Transpose line', async () => {
    window.localStorage.setItem('ahchord.transpose.x1', '2');
    const DETAIL = detail(3);
    vi.mocked((await import('../api/client')).apiSong).mockResolvedValue(DETAIL);
    const root = await mountPlayer();

    expect(container?.textContent).toContain('Capo 3 · Key of A');
    // The old conditional capo line (with "Transpose +2" and the "N / M" prefix) is gone.
    // Tripwire: the closed popover never renders TransposeControl's "Transpose" label —
    // if a future test opens the popover, scope this to the top bar instead.
    expect(container?.textContent).not.toContain('Transpose');
    // The overlay dock and the (kept) keyboard-hints line are present.
    expect(container?.querySelector('[aria-label="Player controls"]')).toBeTruthy();
    expect(container?.textContent).toContain('Esc back');

    await act(async () => root.unmount());
  });

  it('shows the plain key in the neutral case — the slot is never empty', async () => {
    const DETAIL = detail(0);
    vi.mocked((await import('../api/client')).apiSong).mockResolvedValue(DETAIL);
    const root = await mountPlayer();

    expect(container?.textContent).toContain('Key of G');
    // Neutral: no capo value anywhere (the dock's compact "Key & Capo" label
    // legitimately contains the word).
    expect(container?.textContent).not.toMatch(/Capo\s*\d/);

    await act(async () => root.unmount());
  });
});
