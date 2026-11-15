/**
 * MT-05: restore-push helpers — the pure decisions behind the device → server upload.
 * A server that has never seen a cache row is not a deletion: the rows are uploaded first,
 * and pruning only ever covers rows the server can account for (its list, the pushed ids,
 * and the rows a push failed to land — an uploaded row's only copy is never pruned).
 * Split out of sync.ts so the policy is testable without IndexedDB.
 */

import type { PlaylistDetail, PushPlaylist, PushSong, SongDetail } from '../api/types';

/** Per-request caps (server: SyncPushService.MaxSongsPerRequest / MaxPlaylistsPerRequest). */
export const PUSH_SONG_CHUNK = 100;
export const PUSH_PLAYLIST_CHUNK = 50;

export function chunk<T>(items: T[], size: number): T[][] {
  const out: T[][] = [];
  for (let i = 0; i < items.length; i += size) out.push(items.slice(i, i + size));
  return out;
}

/** Cached songs the server has never seen and can restore (rows without text can't be pushed). */
export function localOnlySongs(cached: SongDetail[], serverIds: string[]): SongDetail[] {
  const onServer = new Set(serverIds);
  return cached.filter((s) => !onServer.has(s.id) && s.chordProText);
}

/** Cached playlists the server has never seen. */
export function localOnlyPlaylists(cached: PlaylistDetail[], serverIds: string[]): PlaylistDetail[] {
  const onServer = new Set(serverIds);
  return cached.filter((p) => !onServer.has(p.id));
}

export function toPushSong(s: SongDetail): PushSong {
  return {
    clientId: s.id,
    title: s.title,
    artist: s.artist,
    album: s.album,
    year: s.year,
    key: s.key,
    genre: s.genre,
    tempo: s.tempo,
    capoFret: s.capoFret,
    chordProText: s.chordProText,
    sourceUrl: s.sourceUrl,
  };
}

/** The playlist's songs ride as cache ids; pushed ones follow the server's new ids. */
export function toPushPlaylist(p: PlaylistDetail, songMap: Record<string, string>): PushPlaylist {
  return {
    clientId: p.id,
    name: p.name,
    description: p.description,
    isSetlist: p.isSetlist,
    gigDate: p.gigDate,
    songClientIds: p.songs.map((s) => songMap[s.id] ?? s.id),
  };
}

/**
 * Prune keep-lists from a push response: ids the server mapped (the cache rows re-key to
 * them) plus the client ids whose row failed to land (kept on the device for the retry).
 * Returns the song keep-extras and clientId → serverId map.
 */
export function pushOutcomes(
  results: { clientId: string; id: string; error: string | null }[],
): { keep: string[]; map: Record<string, string> } {
  const keep: string[] = [];
  const map: Record<string, string> = {};
  for (const r of results) {
    if (r.error) keep.push(r.clientId);
    else {
      map[r.clientId] = r.id;
      keep.push(r.id);
    }
  }
  return { keep, map };
}
