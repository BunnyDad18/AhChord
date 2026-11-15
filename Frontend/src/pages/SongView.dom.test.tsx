/**
 * ST-62: StrictMode effect-cycle regression for the song page.
 *
 * React 18 StrictMode runs setup → cleanup → setup on mount. The song-fetch effect
 * recorded its dedupe guard (fetchedRef) during SETUP and the cancelled flag in CLEANUP:
 * setup 2 saw the guard, returned early, and the only in-flight fetch got discarded by
 * cleanup 1 — the page hung on a skeleton forever (dev-only). The fix clears
 * fetchedRef.current in cleanup. This test mounts the page inside <StrictMode> and
 * asserts the chart actually renders; with the old effect ordering apiSong fires once,
 * its result is discarded, and the title never appears.
 */
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { act } from 'react-dom/test-utils';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { SongDetailResponse } from '../api/types';

vi.mock('../api/client', () => ({
  apiSong: vi.fn(() => Promise.resolve(DETAIL)),
  apiSongSourceFiles: vi.fn(() => Promise.resolve([])),
  apiSongView: vi.fn(() => Promise.resolve(null)),
  apiSongUpdate: vi.fn(),
  apiSongVersions: vi.fn(() => Promise.resolve([])),
  apiDeleteSourceFile: vi.fn(),
  apiUploadSourceFile: vi.fn(),
  apiSourceFileBlob: vi.fn(),
}));

import SongView from './SongView';
import { apiSong } from '../api/client';

const DETAIL = {
  song: {
    id: 'x1',
    title: 'ST62 Mount Test',
    artist: 'Agent',
    keyName: 'C',
    year: null,
    album: null,
    genre: null,
    tempo: null,
    createdAt: '2026-11-01T00:00:00Z',
    updatedAt: '2026-11-01T00:00:00Z',
    chordProText: '{title: ST62 Mount Test}\n\n{start_of_verse}\nHello [C] world\n{end_of_verse}\n',
    sourceUrl: null,
  },
  parsed: {
    header: { title: 'ST62 Mount Test' },
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

let container: HTMLDivElement | null = null;

afterEach(() => {
  container?.remove();
  container = null;
  vi.clearAllMocks();
});

async function mountSongPage() {
  (globalThis as Record<string, unknown>).IS_REACT_ACT_ENVIRONMENT = true;
  container = document.createElement('div');
  document.body.appendChild(container);
  const root = createRoot(container);
  await act(async () => {
    root.render(
      <StrictMode>
        <MemoryRouter initialEntries={['/songs/x1']}>
          <Routes>
            <Route path="/songs/:id" element={<SongView />} />
          </Routes>
        </MemoryRouter>
      </StrictMode>,
    );
  });
  // Flush the fetch promise chains (the second setup's fetch is the one that lands).
  await act(async () => {
    await Promise.resolve();
    await Promise.resolve();
  });
  return root;
}

describe('SongView under StrictMode double-invoke (ST-62)', () => {
  it('renders the chart — the cancelled setup-1 fetch does not strand the page', async () => {
    const root = await mountSongPage();

    // Setup 2 refetched (guard cleared in cleanup) ...
    expect(vi.mocked(apiSong).mock.calls.length).toBe(2);
    // ... and its result landed: the page left the loading skeleton and renders the chart
    // (lyric text is split at chord indexes, so assert on fragments, not the joined line).
    expect(container?.textContent).toContain('ST62 Mount Test');
    expect(container?.textContent).toContain('Verse 1');
    expect(container?.textContent).toContain('Hello');

    await act(async () => root.unmount());
  });
});
