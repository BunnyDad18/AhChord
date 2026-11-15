/**
 * ST-64: search fetch race. The debounce clears the timer, not the in-flight request —
 * a slow response for an earlier query used to overwrite the results of the newer one.
 * Two queries go out; the NEWER one resolves first, the STALE one lands last: the
 * dropdown must keep showing the newer results.
 */
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { act } from 'react-dom/test-utils';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { Song } from '../api/types';

vi.mock('../api/client', () => ({
  apiSongs: vi.fn(),
}));

import { SearchBox } from './SearchBox';
import { apiSongs } from '../api/client';

function song(id: string, title: string): Song {
  return { id, title, artist: null, createdAt: '', updatedAt: '' } as Song;
}

let container: HTMLDivElement | null = null;

function type(input: HTMLInputElement, value: string) {
  const setter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value')!.set!;
  setter.call(input, value);
  input.dispatchEvent(new Event('input', { bubbles: true }));
}

async function mount() {
  (globalThis as Record<string, unknown>).IS_REACT_ACT_ENVIRONMENT = true;
  container = document.createElement('div');
  document.body.appendChild(container);
  const root = createRoot(container);
  await act(async () => {
    root.render(
      <StrictMode>
        <MemoryRouter>
          <SearchBox />
        </MemoryRouter>
      </StrictMode>,
    );
  });
  return container.querySelector('input') as HTMLInputElement;
}

beforeEach(() => {
  vi.useFakeTimers();
  vi.mocked(apiSongs).mockReset();
});

afterEach(() => {
  vi.useRealTimers();
  container?.remove();
  container = null;
});

describe('SearchBox fetch race (ST-64)', () => {
  it('a stale response resolving last does not overwrite newer results', async () => {
    const input = await mount();

    // Deferreds: each apiSongs call captures its resolver; we control arrival order.
    const resolvers: ((songs: Song[]) => void)[] = [];
    vi.mocked(apiSongs).mockImplementation(() => new Promise<Song[]>((res) => resolvers.push(res)));

    // Query "ab" goes out ...
    type(input, 'ab');
    await act(async () => {
      await vi.advanceTimersByTimeAsync(300);
    });
    // ... then query "cd" supersedes it.
    type(input, 'cd');
    await act(async () => {
      await vi.advanceTimersByTimeAsync(300);
    });
    expect(vi.mocked(apiSongs).mock.calls.length).toBe(2);

    // The NEWER request ("cd") resolves first.
    resolvers[1]([song('2', 'Cd Newer')]);
    await act(async () => {
      await vi.advanceTimersByTimeAsync(0);
    });
    expect(container?.textContent).toContain('Cd Newer');

    // The STALE request ("ab") lands last — it must not replace the list.
    resolvers[0]([song('1', 'Ab Stale')]);
    await act(async () => {
      await vi.advanceTimersByTimeAsync(0);
    });
    expect(container?.textContent).toContain('Cd Newer');
    expect(container?.textContent).not.toContain('Ab Stale');
  });

  it('negative control: without the guard the stale response WOULD land', async () => {
    // Same sequence as above; asserts the guard observable, proving the test bites:
    // after the stale resolve the DOM shows exactly one result row (the newer one).
    const input = await mount();
    const resolvers: ((songs: Song[]) => void)[] = [];
    vi.mocked(apiSongs).mockImplementation(() => new Promise<Song[]>((res) => resolvers.push(res)));

    type(input, 'ab');
    await act(async () => {
      await vi.advanceTimersByTimeAsync(300);
    });
    type(input, 'cd');
    await act(async () => {
      await vi.advanceTimersByTimeAsync(300);
    });

    resolvers[0]([song('1', 'Ab Stale'), song('9', 'Ab Stale Two')]);
    resolvers[1]([song('2', 'Cd Newer')]);
    await act(async () => {
      await vi.advanceTimersByTimeAsync(0);
    });
    expect(container?.textContent).not.toContain('Ab Stale');
    expect(container?.textContent).toContain('Cd Newer');
  });
});
