// @vitest-environment jsdom
/**
 * ST-87: the phone/tablet LIVE SESSIONS bar — host + view + Join per live
 * follow session; Join invokes the hub; nothing running → nothing rendered.
 */
import { createRoot } from 'react-dom/client';
import { act } from 'react-dom/test-utils';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

vi.mock('../hooks/useActiveSessions', () => ({
  useActiveSessions: () => [
    {
      hostId: 'host-1',
      hostName: 'Ada',
      kind: 'song' as const,
      songId: 's-1',
      songTitle: 'Amazing Grace',
      songArtist: 'Trad',
      playlistId: null,
      songIndex: null,
      followers: 2,
      startedAt: '2026-11-09T10:00:00Z',
    },
  ],
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
  followers: 2,
});
vi.mock('../realtime/sessionClient', () => ({
  getSharedConnection: () => Promise.resolve({ on: vi.fn(), invoke }),
}));

import { LiveSessions } from './LiveSessions';
import { useAuthStore } from '../stores/auth';
import { useSessionStore } from '../stores/session';

let container: HTMLDivElement | null = null;
let root: ReturnType<typeof createRoot> | null = null;

async function mount() {
  (globalThis as Record<string, unknown>).IS_REACT_ACT_ENVIRONMENT = true;
  container = document.createElement('div');
  document.body.appendChild(container);
  root = createRoot(container);
  await act(async () => {
    root!.render(<LiveSessions />);
  });
}

beforeEach(() => {
  useSessionStore.getState().reset();
  useAuthStore.setState({ user: { id: 'me-1' } as never, token: 't' });
});

afterEach(async () => {
  await act(async () => root?.unmount());
  container?.remove();
  container = null;
  root = null;
  invoke.mockClear();
});

describe('LiveSessions (ST-87 live-session bar)', () => {
  it('lists each live session with host, view and a Join button', async () => {
    await mount();
    const text = container!.textContent ?? '';
    expect(text).toContain('Ada');
    expect(text).toContain('Amazing Grace · 2 following');
    expect(container!.querySelector('button[aria-label="Join Ada\'s session"]')).not.toBeNull();
  });

  it('Join invokes the follow hub', async () => {
    await mount();
    const btn = container!.querySelector('button[aria-label="Join Ada\'s session"]')!;
    await act(async () => {
      btn.dispatchEvent(new window.MouseEvent('click', { bubbles: true }));
    });
    expect(invoke).toHaveBeenCalledWith('JoinFollow', 'host-1');
  });
});
