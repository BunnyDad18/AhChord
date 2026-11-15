import { useState } from 'react';

/** ST-42: chart column layout — 'auto' (fit one screen) or a fixed count. */
export type ColumnPref = 'auto' | 1 | 2 | 3;

const KEY = 'ahchord.columns';

function read(): ColumnPref {
  const v = window.localStorage.getItem(KEY);
  if (v === 'auto' || v === '1' || v === '2' || v === '3') return v as ColumnPref;
  return 'auto';
}

/**
 * ST-42: per-browser column preference, shared by the song page and the player
 * (same pattern as the font-scale / chord-mode prefs in SongView).
 */
export function useColumnPref(): [ColumnPref, (p: ColumnPref) => void] {
  const [pref, setPrefState] = useState<ColumnPref>(read);
  const setPref = (p: ColumnPref) => {
    setPrefState(p);
    window.localStorage.setItem(KEY, String(p));
  };
  return [pref, setPref];
}
