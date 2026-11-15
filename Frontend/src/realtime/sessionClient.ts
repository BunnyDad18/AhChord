import type { HubConnection } from '@microsoft/signalr';
import { HubConnectionBuilder, HubConnectionState } from '@microsoft/signalr';
import { useAuthStore } from '../stores/auth';
import { hubUrl } from '../api/server';

export interface SessionState {
  playlistId: string;
  songIndex: number;
  capo: number;
}

export interface Participants {
  playlistId: string;
  count: number;
  userIds: string[];
}

let conn: HubConnection | null = null;
let starting: Promise<HubConnection> | null = null;
let restarting: Promise<void> | null = null;

// ST-64: SignalR's onreconnected is a SINGLE-callback setter — the first registration
// silently replaces the previous one (the import queue's refresh was killed whenever a
// player session joined). sessionClient is the one owner; app-wide consumers register
// here and get a real unsubscribe.
type ReconnectedListener = () => void;
const reconnectedListeners = new Set<ReconnectedListener>();

/** Register a hub-reconnected listener; returns the unsubscribe. */
export function onReconnected(listener: ReconnectedListener): () => void {
  reconnectedListeners.add(listener);
  return () => reconnectedListeners.delete(listener);
}

function fireReconnected() {
  // Copy so a listener unsubscribing during the fan-out cannot skip its successors.
  for (const l of [...reconnectedListeners]) l();
}

/**
 * One shared hub connection for the whole app (ST-25). The JWT comes from the
 * auth store via accessTokenFactory, so the handshake is authenticated and
 * unauthenticated joins are rejected server-side (401).
 */
function ensureConnection(): Promise<HubConnection> {
  if (conn) {
    const c = conn;
    if (c.state === HubConnectionState.Disconnected) {
      // ST-55: cache the restart promise — concurrent callers must not both call
      // start() (SignalR rejects the second with "Cannot start while connecting").
      if (!restarting) {
        restarting = c.start().finally(() => {
          restarting = null;
        });
      }
      return restarting.then(() => c);
    }
    return Promise.resolve(c);
  }
  if (!starting) {
    const c = new HubConnectionBuilder()
      .withUrl(hubUrl(), {
        // Empty string when signed out: the hub handshake is rejected (401).
        accessTokenFactory: () => useAuthStore.getState().token ?? '',
      })
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .build();
    // ST-64: the single onreconnected registration, fanning out to the listener set.
    c.onreconnected(fireReconnected);
    starting = c
      .start()
      .then(() => {
        conn = c;
        return c;
      })
      .catch((err) => {
        starting = null;
        throw err;
      });
  }
  return starting;
}

/**
 * ST-33: other realtime modules (the import queue) share this one connection —
 * the server pushes per-user events over it via Clients.User.
 */
export function getSharedConnection(): Promise<HubConnection> {
  return ensureConnection();
}

export interface SessionHandlers {
  onState?: (state: SessionState) => void;
  onParticipants?: (p: Participants) => void;
  onReconnecting?: (reconnecting: boolean) => void;
  /** ST-55: fires when automatic reconnection gave up (permanent close), so the
   * UI can surface it and offer a manual rejoin instead of a dead "Reconnecting…". */
  onClosed?: () => void;
}

export interface SessionHandle {
  next: () => Promise<void>;
  prev: () => Promise<void>;
  /** ST-55: session transpose, −12…+12 semitones (hub method renamed from SetCapo — the
   * client always sent the ST-39 transpose range; the 0–6 validation desynced sessions). */
  setTranspose: (semitones: number) => Promise<void>;
  /** ST-55: move the session to a song index (setlist random must go through the hub). */
  goTo: (index: number) => Promise<void>;
  leave: () => Promise<void>;
}

/**
 * Joins the playlist session and wires up the server->client events.
 * Returns a handle for the client->server actions plus a leave().
 * Conflict policy: last write wins — a received state change is applied by the
 * caller unconditionally (it wins over in-flight local state).
 */
export async function joinSession(
  playlistId: string,
  songIndex: number | undefined,
  handlers: SessionHandlers = {},
): Promise<SessionHandle> {
  const c = await ensureConnection();

  const stateHandler = (s: SessionState) => handlers.onState?.(s);
  const participantsHandler = (p: Participants) => handlers.onParticipants?.(p);
  c.on('sessionState', stateHandler);
  c.on('participants', participantsHandler);
  c.onreconnecting(() => handlers.onReconnecting?.(true));
  // ST-64: via the shared fan-out (the raw setter is single-callback and was stolen by
  // / stole the import queue's refresh). Registered per join, dropped on leave.
  const offReconnected = onReconnected(() => handlers.onReconnecting?.(false));
  // ST-55: SignalR v10's onclose is a single-callback registration (no public off) —
  // the session player is its owner. A permanent close surfaces the dead-connection banner.
  c.onclose(() => handlers.onClosed?.());

  const cleanup = () => {
    c.off('sessionState', stateHandler);
    c.off('participants', participantsHandler);
    offReconnected();
  };

  try {
    await c.invoke('Join', playlistId, songIndex ?? null);
  } catch (e) {
    cleanup();
    throw e;
  }

  return {
    next: async () => {
      await c.invoke('Next', playlistId);
    },
    prev: async () => {
      await c.invoke('Prev', playlistId);
    },
    setTranspose: async (semitones: number) => {
      await c.invoke('SetTranspose', playlistId, semitones);
    },
    goTo: async (index: number) => {
      await c.invoke('GoTo', playlistId, index);
    },
    leave: async () => {
      cleanup();
      try {
        await c.invoke('Leave', playlistId);
      } catch {
        /* connection may already be gone */
      }
    },
  };
}
