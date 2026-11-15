import { create } from 'zustand';

/** ST-51 #3: per-song transpose, persisted to localStorage keyed by song id. */
const STORAGE_PREFIX = 'ahchord.transpose.';

function clamp(n: number): number {
  return Math.max(-12, Math.min(12, Math.round(n)));
}

function readStored(songId: string): number {
  try {
    const raw = localStorage.getItem(STORAGE_PREFIX + songId);
    if (raw === null) return 0;
    const n = Number(raw);
    return Number.isFinite(n) ? clamp(n) : 0;
  } catch {
    return 0; // storage unavailable — fall back to as-written
  }
}

function writeStored(songId: string, n: number): void {
  try {
    localStorage.setItem(STORAGE_PREFIX + songId, String(n));
  } catch {
    /* storage unavailable — session-only */
  }
}

interface TransposeState {
  /** Session transpose in semitones, -12…+12 (ST-39 E1). Per-song since ST-51 #3: each song
   * remembers its own value across reloads; values no longer carry over between songs. */
  transpose: number;
  /** The song the current value belongs to (null until a page loads one via setForSong). */
  songId: string | null;
  /** Load the stored per-song value when a song is opened. No-op if already loaded for this song. */
  setForSong: (songId: string) => void;
  setTranspose: (semitones: number) => void;
  /** ST-55: set the value WITHOUT persisting it to the current songId. Setlist mode uses this —
   * the session hub owns the set's transpose, and the store's songId is the last song page
   * opened, so persisting there leaked setlist transposes onto unrelated songs. */
  setTransient: (semitones: number) => void;
  bump: (delta: number, transient?: boolean) => void;
}

export const useTransposeStore = create<TransposeState>()((set, get) => ({
  transpose: 0,
  songId: null,
  setForSong: (songId) => {
    if (get().songId === songId) return;
    set({ songId, transpose: readStored(songId) });
  },
  setTranspose: (n) => {
    const value = clamp(n);
    const id = get().songId;
    if (id !== null) writeStored(id, value);
    set({ transpose: value });
  },
  setTransient: (n) => set({ transpose: clamp(n) }),
  bump: (delta, transient) =>
    transient
      ? get().setTransient(get().transpose + delta)
      : get().setTranspose(get().transpose + delta),
}));
