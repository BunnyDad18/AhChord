/**
 * ST-64: the onreconnected fan-out. SignalR's onreconnected is a SINGLE-callback setter —
 * joinSession's registration used to replace the import queue's (queue reconciliation died
 * app-wide while a setlist session was open). sessionClient now owns the one registration
 * and multiplexes to a listener set with real unsubscribes.
 *
 * Runs in the node environment with a fake SignalR connection (no DOM needed).
 */
import { describe, expect, it, vi } from 'vitest';

class FakeConnection {
  onreconnectedCb: (() => void) | null = null;
  onreconnectingCb: (() => void) | null = null;
  oncloseCb: (() => void) | null = null;
  on() { /* server-event handlers: not exercised here */ }
  off() { /* */ }
  onreconnected(cb: () => void) {
    this.onreconnectedCb = cb; // single-callback setter — the property the bug lives in
  }
  onreconnecting(cb: () => void) {
    this.onreconnectingCb = cb;
  }
  onclose(cb: () => void) {
    this.oncloseCb = cb;
  }
  async start() { /* */ }
  async stop() { /* */ }
  async invoke() { /* */ }
}

const fakeConn = new FakeConnection();

vi.mock('@microsoft/signalr', () => ({
  HubConnectionBuilder: class {
    withUrl() {
      return this;
    }
    withAutomaticReconnect() {
      return this;
    }
    build() {
      return fakeConn;
    }
  },
  HubConnectionState: { Disconnected: 0, Connected: 1, Connecting: 2, Disconnecting: 3 },
}));

vi.mock('../stores/auth', () => ({
  useAuthStore: { getState: () => ({ token: null }) },
}));

import { getSharedConnection, onReconnected } from './sessionClient';

describe('onReconnected fan-out (ST-64)', () => {
  it('delivers to every live listener and skips the unsubscribed one', async () => {
    await getSharedConnection(); // builds the connection and installs the single owner

    const a = vi.fn();
    const b = vi.fn();
    const offA = onReconnected(a);
    onReconnected(b);

    offA();
    fakeConn.onreconnectedCb!(); // simulate the hub reconnecting

    expect(a).not.toHaveBeenCalled();
    expect(b).toHaveBeenCalledTimes(1);
  });

  it('survives a listener unsubscribing DURING the fan-out', async () => {
    const first = vi.fn(() => offSecond());
    const second = vi.fn();
    const offFirst = onReconnected(first);
    const offSecond = onReconnected(second);

    fakeConn.onreconnectedCb!();

    expect(first).toHaveBeenCalledTimes(1);
    expect(second).toHaveBeenCalledTimes(1); // iterated over a copy — not skipped by first's removal
    offFirst();
  });

  it('a later registration coexists (the setter-clobbering is gone)', async () => {
    const queue = vi.fn(); // e.g. Shell's import-queue refresh registered first
    const session = vi.fn(); // e.g. a player join registered second — this used to kill `queue`
    const offQ = onReconnected(queue);
    const offS = onReconnected(session);

    fakeConn.onreconnectedCb!();

    expect(queue).toHaveBeenCalledTimes(1);
    expect(session).toHaveBeenCalledTimes(1);
    offQ();
    offS();
  });
});
