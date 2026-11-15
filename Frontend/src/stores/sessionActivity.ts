import { create } from 'zustand';

/**
 * ST-70g: global "is the user in a playing session" flag for the top-bar
 * activity box. Set by the Player when it joins/leaves a setlist session.
 * (The session lives in the Player today; this store makes the activity
 * registry complete so a session can outlive the player page later.)
 */
interface SessionActivityState {
  active: boolean;
  setActive: (active: boolean) => void;
}

export const useSessionActivityStore = create<SessionActivityState>()((set) => ({
  active: false,
  setActive: (active) => set({ active }),
}));
