import { create } from 'zustand';
import type { FollowStatePayload } from '../api/types';

/**
 * ST-87: the user's role in the general follow-session service.
 *
 *   idle     — not hosting, not following.
 *   host     — MY session is live: every song I open is broadcast to my followers.
 *   follower — I joined someone's session: their view drives navigation (SessionWatcher).
 *
 * One role at a time; the role is in-memory (a page reload returns to idle, the
 * documented MVP limit). followClient.ts owns the transitions.
 */
interface SessionStoreState {
  role: 'idle' | 'host' | 'follower';
  /** host: my user id; follower: the host I follow. */
  hostId: string;
  hostName: string;
  /** host: followers joined; follower: siblings (informational). */
  followers: number;
  /** Last known view — mine (host) or the host's (follower). */
  viewing: FollowStatePayload | null;
  startHost: (p: FollowStatePayload) => void;
  setFollower: (p: FollowStatePayload) => void;
  /** 'followState' event — applied for host (echo/count) and follower (the view). */
  applyView: (p: FollowStatePayload) => void;
  reset: () => void;
}

export const useSessionStore = create<SessionStoreState>()((set) => ({
  role: 'idle',
  hostId: '',
  hostName: '',
  followers: 0,
  viewing: null,
  startHost: (p) => set({ role: 'host', hostId: p.hostId, hostName: p.hostName, followers: 0, viewing: p }),
  setFollower: (p) => set({ role: 'follower', hostId: p.hostId, hostName: p.hostName, followers: p.followers, viewing: p }),
  applyView: (p) =>
    set((s) => (s.role === 'idle' ? s : { viewing: p, followers: p.followers })),
  reset: () => set({ role: 'idle', hostId: '', hostName: '', followers: 0, viewing: null }),
}));
