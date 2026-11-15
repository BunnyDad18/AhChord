import type { HubConnection } from '@microsoft/signalr';
import type { FollowStatePayload } from '../api/types';
import { getSharedConnection } from './sessionClient';
import { useSessionStore } from '../stores/session';

/**
 * ST-87: the general follow-session client — "Start a Session" (host: everything
 * I browse is broadcast) and "Join" (follower: I get the host's view). Rides the
 * app's one shared hub connection (ST-33/ST-64); server methods: SessionHub.
 *
 * The event handlers are registered once on the connection (it survives
 * automatic reconnects, so the handlers survive too); the store decides whether
 * an event applies for the current role.
 */
let wired = false;

function wire(c: HubConnection) {
  if (wired) return;
  wired = true;
  c.on('followState', (p: FollowStatePayload) => {
    const s = useSessionStore.getState();
    if (s.role === 'idle') return;
    // Host: only my own session's echo (count). Follower: only my host's view.
    if (p.hostId !== s.hostId) return;
    s.applyView(p);
  });
  c.on('followEnded', (hostId: string) => {
    const s = useSessionStore.getState();
    if (s.role === 'follower' && s.hostId === hostId) s.reset();
  });
}

/** Start (or re-register) my session — I become the host. */
export async function startHosting(): Promise<void> {
  const c = await getSharedConnection();
  wire(c);
  const p = await c.invoke<FollowStatePayload>('StartFollow');
  useSessionStore.getState().startHost(p);
}

/**
 * The host publishes what they are looking at (fire-and-forget; a failed publish
 * on a dead connection is swallowed — the session's next real action re-starts it).
 */
export async function publishFollow(view: {
  kind: 'song' | 'setlist';
  songId?: string | null;
  songTitle?: string | null;
  songArtist?: string | null;
  playlistId?: string | null;
  songIndex?: number | null;
}): Promise<void> {
  if (useSessionStore.getState().role !== 'host') return;
  try {
    const c = await getSharedConnection();
    await c.invoke('PublishFollow', view.kind, view.songId ?? null, view.songTitle ?? null,
      view.songArtist ?? null, view.playlistId ?? null, view.songIndex ?? null);
  } catch {
    /* connection down — the UI keeps the host role; next publish rides the reconnect */
  }
}

/** Join a host's session — I become the follower (their current view applies at once). */
export async function joinHosting(hostId: string): Promise<FollowStatePayload> {
  const c = await getSharedConnection();
  wire(c);
  const p = await c.invoke<FollowStatePayload>('JoinFollow', hostId);
  useSessionStore.getState().setFollower(p);
  return p;
}

/** The host ends the session (followers are told server-side). */
export async function stopHosting(): Promise<void> {
  try {
    const c = await getSharedConnection();
    await c.invoke('EndFollow');
  } catch {
    /* connection down — the local role resets regardless */
  }
  useSessionStore.getState().reset();
}

/** The follower leaves the host's session. */
export async function leaveFollowing(): Promise<void> {
  try {
    const c = await getSharedConnection();
    await c.invoke('LeaveFollow');
  } catch {
    /* best effort */
  }
  useSessionStore.getState().reset();
}
