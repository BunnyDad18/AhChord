/**
 * ST-64: optimistic reorder rollback must compose with concurrent moves.
 * Fast ↑↑ clicks fire two reorders; the FIRST PUT fails, the second succeeds.
 * The old code rolled back to the render-closure snapshot (the order before the
 * FIRST move), silently discarding the successful second move. The fix applies
 * the inverse swap (by song id) to the current order — so only the failed move
 * is undone.
 */
import { createRoot } from 'react-dom/client';
import { act } from 'react-dom/test-utils';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { PlaylistDetail as PlaylistDetailDto, Song } from '../api/types';

vi.mock('../api/client', () => ({
  apiPlaylist: vi.fn(),
  apiCollections: vi.fn(() => Promise.resolve([])),
  apiPlaylistAddSong: vi.fn(),
  apiPlaylistRemoveSong: vi.fn(),
  apiPlaylistReorder: vi.fn(),
  apiPlaylistUpdate: vi.fn(),
  apiSongs: vi.fn(() => Promise.resolve([])),
}));

import PlaylistDetail from './PlaylistDetail';
import { apiPlaylist, apiPlaylistReorder } from '../api/client';

function song(id: string, title: string): Song {
  return { id, title, artist: null, createdAt: '', updatedAt: '' } as Song;
}

const ABC = [song('a', 'Alpha'), song('b', 'Bravo'), song('c', 'Charlie')];

let container: HTMLDivElement | null = null;
let root: ReturnType<typeof createRoot> | null = null;

function titles(): string[] {
  return [...(container?.querySelectorAll('ol strong') ?? [])].map((el) => el.textContent ?? '');
}

async function mountPlaylist() {
  (globalThis as Record<string, unknown>).IS_REACT_ACT_ENVIRONMENT = true;
  vi.mocked(apiPlaylist).mockResolvedValue({
    id: 'pl1',
    name: 'ST64 Race Playlist',
    isSetlist: true,
    description: null,
    collectionId: null,
    songs: ABC,
  } as unknown as PlaylistDetailDto);

  container = document.createElement('div');
  document.body.appendChild(container);
  root = createRoot(container);
  await act(async () => {
    root!.render(
      <MemoryRouter initialEntries={['/playlists/pl1']}>
        <Routes>
          <Route path="/playlists/:id" element={<PlaylistDetail />} />
        </Routes>
      </MemoryRouter>,
    );
  });
}

// ST-75: reorder lives in the row's ⋯ menu (labeled "Move up"/"Move down").
async function clickMove(title: string, dir: 'up' | 'down') {
  const trigger = container!.querySelector(
    `button[aria-label="Actions for ${title}"]`,
  ) as HTMLButtonElement;
  await act(async () => {
    trigger.dispatchEvent(new MouseEvent('click', { bubbles: true }));
  });
  const item = [...(container?.querySelectorAll('[role="menu"] button') ?? [])].find(
    (b) => (b.textContent ?? '').trim() === `Move ${dir}`,
  ) as HTMLButtonElement;
  await act(async () => {
    item.dispatchEvent(new MouseEvent('click', { bubbles: true }));
  });
}

beforeEach(() => {
  vi.mocked(apiPlaylistReorder).mockReset();
});

afterEach(async () => {
  await act(async () => root?.unmount());
  container?.remove();
  container = null;
});

describe('PlaylistDetail reorder rollback (ST-64)', () => {
  it('a failed first move rolls back alone — the successful second move survives', async () => {
    await mountPlaylist();
    expect(titles()).toEqual(['Alpha', 'Bravo', 'Charlie']);

    // The first PUT stays PENDING (a real request in flight while the second click happens);
    // the second PUT resolves immediately. The first is rejected explicitly after both moves.
    let rejectFirst: (e: Error) => void = () => {};
    const first = new Promise<void>((_, rej) => {
      rejectFirst = rej;
    });
    vi.mocked(apiPlaylistReorder)
      .mockImplementationOnce(() => first)
      .mockImplementation(async () => {});

    // Bravo up (Alpha<->Bravo, PUT pending) then Charlie up (on the swapped order).
    await clickMove('Bravo', 'up');
    expect(titles()).toEqual(['Bravo', 'Alpha', 'Charlie']); // optimistic
    await clickMove('Charlie', 'up');
    expect(titles()).toEqual(['Bravo', 'Charlie', 'Alpha']); // second optimistic
    expect(apiPlaylistReorder).toHaveBeenCalledTimes(2);

    // The first PUT fails → inverse-swap rollback of Alpha<->Bravo ONLY.
    await act(async () => {
      rejectFirst(new Error('boom'));
      await Promise.resolve();
      await Promise.resolve();
    });

    // The old snapshot rollback produced [Alpha, Bravo, Charlie] (move 2 lost).
    expect(titles()).toEqual(['Alpha', 'Charlie', 'Bravo']);
  });
});

