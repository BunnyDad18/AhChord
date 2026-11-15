// @vitest-environment jsdom
/**
 * ST-87: the sidebar SESSION panel — the general follow service. Idle: live
 * sessions (host · view + Join) and the ALWAYS-visible Start session button;
 * hosting swaps in the live strip with Stop; joining swaps in the Following
 * strip with Leave; the collapsed rail is a users icon carrying the count.
 * Sessions hosted by my own account (another device) are joinable — following
 * myself onto the tablet is the primary case.
 */
import { createRoot } from 'react-dom/client';
import { act } from 'react-dom/test-utils';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const { sessionsRef } = vi.hoisted(() => ({
  sessionsRef: {
    list: [
      {
        hostId: 'host-1',
        hostName: 'Ada',
        kind: 'song' as const,
        songId: 's-1',
        songTitle: 'Amazing Grace',
        songArtist: 'Trad',
        playlistId: null,
        songIndex: null,
        followers: 1,
        startedAt: '2026-11-09T10:00:00Z',
      },
    ],
  },
}));

vi.mock('../hooks/useActiveSessions', () => ({
  useActiveSessions: () => sessionsRef.list,
}));

const invoke = vi.fn().mockResolvedValue({
  hostId: 'host-1',
  hostName: 'Ada',
  kind: 'song',
  songId: 's-1',
  songTitle: 'Amazing Grace',
  songArtist: 'Trad',
  playlistId: null,
  songIndex: null,
  seq: 1,
  followers: 1,
});
vi.mock('../realtime/sessionClient', () => ({
  getSharedConnection: () => Promise.resolve({ on: vi.fn(), invoke }),
}));

import { SessionPanel } from './SessionPanel';
import { useSessionStore } from '../stores/session';

let container: HTMLDivElement | null = null;
let root: ReturnType<typeof createRoot> | null = null;

async function mount(collapsed = false) {
  (globalThis as Record<string, unknown>).IS_REACT_ACT_ENVIRONMENT = true;
  container = document.createElement('div');
  document.body.appendChild(container);
  root = createRoot(container);
  await act(async () => {
    root!.render(
      <MemoryRouter>
        <SessionPanel collapsed={collapsed} />
      </MemoryRouter>,
    );
  });
}

function click(selector: string) {
  const btn = container!.querySelector(selector)!;
  return act(async () => {
    btn.dispatchEvent(new window.MouseEvent('click', { bubbles: true }));
  });
}

beforeEach(() => {
  useSessionStore.getState().reset();
});

afterEach(async () => {
  await act(async () => root?.unmount());
  container?.remove();
  container = null;
  root = null;
  invoke.mockClear();
});

describe('SessionPanel (ST-87 sidebar follow service)', () => {
  it('idle: live sessions with Join, and the always-visible Start session button', async () => {
    await mount();
    const text = container!.textContent ?? '';
    expect(text).toContain('Ada · Amazing Grace');
    expect(container!.querySelector('button[aria-label="Join Ada\'s session"]')).not.toBeNull();
    expect(text).toContain('Start session');
  });

  it('a session hosted by my OWN account (another device) is joinable', async () => {
    sessionsRef.list = [{ ...sessionsRef.list[0], hostId: 'me-1', hostName: 'Me' }];
    await mount();
    expect(container!.querySelector('button[aria-label="Join Me\'s session"]')).not.toBeNull();
    sessionsRef.list = [{ ...sessionsRef.list[0], hostId: 'host-1', hostName: 'Ada' }];
  });

  it('Start session hosts, and the panel swaps to the live-session strip', async () => {
    await mount();
    const btn = [...container!.querySelectorAll('button')].find((b) =>
      (b.textContent ?? '').includes('Start session'),
    )!;
    await act(async () => {
      btn.dispatchEvent(new window.MouseEvent('click', { bubbles: true }));
    });
    expect(invoke).toHaveBeenCalledWith('StartFollow');
    const text = container!.textContent ?? '';
    expect(text).toContain('Your session ·');
    expect(text).toContain('Stop session');
  });

  it('Join joins the host’s session and swaps in the Following strip', async () => {
    await mount();
    await click('button[aria-label="Join Ada\'s session"]');
    expect(invoke).toHaveBeenCalledWith('JoinFollow', 'host-1');
    const text = container!.textContent ?? '';
    expect(text).toContain('Following Ada');
    expect(text).toContain('Leave session');
  });

  it('the hosting device shows its own strip, not the list', async () => {
    useSessionStore.getState().startHost({
      hostId: 'me-1',
      hostName: 'Me',
      kind: 'song',
      songId: null,
      songTitle: null,
      songArtist: null,
      playlistId: null,
      songIndex: null,
      seq: 0,
      followers: 0,
    });
    await mount();
    const text = container!.textContent ?? '';
    expect(text).toContain('Your session ·');
    expect(container!.querySelector('button[aria-label="Join Ada\'s session"]')).toBeNull();
  });

  it('collapsed: a users icon button carrying the count', async () => {
    await mount(true);
    expect(container!.querySelector('button[aria-label="1 live session"]')).not.toBeNull();
  });
});
