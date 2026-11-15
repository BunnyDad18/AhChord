/**
 * MT-04: library sync — fetch /api/sync (full on the first run, updatedSince delta after)
 * and write it into the IndexedDB cache. Triggered by Shell on mount, by the browser's
 * `online` event, and by the SignalR reconnect fan-out. Concurrent calls share one run.
 *
 * MT-05 restore push: before the pull writes anything, cache rows the server has never
 * seen are UPLOADED (POST /api/sync/push). A rebuilt/empty server therefore receives the
 * device's library instead of pruning it away — pruning only ever covers rows the server
 * can account for. A push failure aborts the run before any pruning: the cache keeps
 * every row and the next trigger retries.
 */

import { apiSync, apiSyncPush } from '../api/client';
import { useAuthStore } from '../stores/auth';
import { useNetworkStore } from '../stores/network';
import {
  allPlaylists,
  allSongs,
  getMeta,
  prunePlaylists,
  pruneSongs,
  putPlaylists,
  putSongs,
  remapPlaylists,
  remapSongs,
  setMeta,
} from './db';
import {
  chunk,
  localOnlyPlaylists,
  localOnlySongs,
  pushOutcomes,
  PUSH_PLAYLIST_CHUNK,
  PUSH_SONG_CHUNK,
  toPushPlaylist,
  toPushSong,
} from './push';

let running: Promise<void> | null = null;

/**
 * Sync the cache. No-op without a token. `force` re-pulls the full library (ignored when
 * a run is already in flight). Failures are surfaced to the caller (pages/toast); the
 * next trigger retries.
 */
export function syncLibrary(force = false): Promise<void> {
  if (!useAuthStore.getState().token) return Promise.resolve();
  if (running) return running;
  running = doSync(force).finally(() => {
    running = null;
  });
  return running;
}

async function doSync(force: boolean): Promise<void> {
  const last = force ? undefined : ((await getMeta<string>('lastSyncAt')) ?? undefined);
  const payload = await apiSync(last);

  // Restore push (MT-05) — upload rows the server has never seen before the pruning below.
  // The keep-lists widen as rows land: server rows, re-keyed pushed rows, and rows whose
  // push failed (the device copy survives for the retry).
  const keepSongs = new Set(payload.songIds);
  const songMap: Record<string, string> = {};
  for (const group of chunk(localOnlySongs(await allSongs(), payload.songIds).map(toPushSong), PUSH_SONG_CHUNK)) {
    const { keep, map } = pushOutcomes((await apiSyncPush({ songs: group })).songs);
    keep.forEach((id) => keepSongs.add(id));
    Object.assign(songMap, map);
  }
  await remapSongs(songMap);

  const keepPlaylists = new Set(payload.playlistIds);
  const playlistMap: Record<string, string> = {};
  for (const group of chunk(localOnlyPlaylists(await allPlaylists(), payload.playlistIds).map((p) => toPushPlaylist(p, songMap)), PUSH_PLAYLIST_CHUNK)) {
    const { keep, map } = pushOutcomes((await apiSyncPush({ playlists: group })).playlists);
    keep.forEach((id) => keepPlaylists.add(id));
    Object.assign(playlistMap, map);
  }
  await remapPlaylists(playlistMap, songMap);

  // Pull — server rows win for ids the server already had; pushed rows arrive on the next
  // delta (their push bumped UpdatedAt past `lastSyncAt`) and converge to the server shape.
  await putSongs(payload.songs);
  await pruneSongs([...keepSongs]);
  await putPlaylists(payload.playlists);
  await prunePlaylists([...keepPlaylists]);
  await setMeta('collections', payload.collections);
  await setMeta('songCount', payload.songIds.length);
  // The stamp is the server's clock, so the next delta compares like-for-like.
  await setMeta('lastSyncAt', payload.serverTime);

  // A sync that lands means the server is back — clear the boot-probe flag.
  useNetworkStore.getState().setServerUnreachable(false);
  useNetworkStore.getState().setLastSyncAt(payload.serverTime);
}
