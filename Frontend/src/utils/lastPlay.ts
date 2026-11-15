/**
 * ST-75: "last played" tracking for the dashboard's Jump back in hero.
 * Deliberately localStorage-only (per device, no API, no sync): the hero answers
 * "what did I open on THIS device last?" — the band's shared view of recents is
 * the server-side /songs/recent feed the dashboard lists below the hero.
 */

export interface LastPlay {
  /** A setlist player entry (Play reopens the setlist from its top) or a single song. */
  type: 'song' | 'setlist';
  id: string;
  title: string;
  artist?: string | null;
  /** When it was played (ms epoch). */
  at: number;
}

const KEY = 'ahchord.lastPlay';

export function recordLastPlay(p: Omit<LastPlay, 'at'>): void {
  try {
    window.localStorage.setItem(KEY, JSON.stringify({ ...p, at: Date.now() }));
  } catch {
    /* private mode / quota — the hero is a convenience, never a crash */
  }
}

export function readLastPlay(): LastPlay | null {
  try {
    const raw = window.localStorage.getItem(KEY);
    if (!raw) return null;
    const v = JSON.parse(raw) as LastPlay;
    if ((v.type === 'song' || v.type === 'setlist') && typeof v.id === 'string' && typeof v.title === 'string') {
      return v;
    }
    return null;
  } catch {
    return null;
  }
}
