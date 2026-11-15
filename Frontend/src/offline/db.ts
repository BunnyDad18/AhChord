/**
 * MT-04: the offline library cache (IndexedDB).
 *
 * Stores:
 *  - `songs`      — SongDetail (includes chordProText), keyed by id
 *  - `playlists`  — PlaylistDetail (with its songs in order), keyed by id
 *  - `meta`       — small keyed values: lastSyncAt, collections, songCount, …
 *
 * The cache is disposable by design (conflict policy: server wins) — everything in here is
 * rebuilt by a full sync. Raw IndexedDB, no library: three tiny object stores.
 */

import type { Collection, PlaylistDetail, SongDetail } from '../api/types';

const DB_NAME = 'ahchord-offline';
const DB_VERSION = 1;
const STORE_SONGS = 'songs';
const STORE_PLAYLISTS = 'playlists';
const STORE_META = 'meta';

let dbPromise: Promise<IDBDatabase> | null = null;

function openDb(): Promise<IDBDatabase> {
  if (dbPromise) return dbPromise;
  dbPromise = new Promise<IDBDatabase>((resolve, reject) => {
    const req = indexedDB.open(DB_NAME, DB_VERSION);
    req.onupgradeneeded = () => {
      const db = req.result;
      if (!db.objectStoreNames.contains(STORE_SONGS)) db.createObjectStore(STORE_SONGS, { keyPath: 'id' });
      if (!db.objectStoreNames.contains(STORE_PLAYLISTS)) db.createObjectStore(STORE_PLAYLISTS, { keyPath: 'id' });
      if (!db.objectStoreNames.contains(STORE_META)) db.createObjectStore(STORE_META);
    };
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(req.error);
    req.onblocked = () => reject(new Error('IndexedDB open blocked'));
  });
  return dbPromise;
}

/** Run `fn` inside one readwrite transaction; resolves when it commits. */
function tx<T>(stores: string[], mode: IDBTransactionMode, fn: (t: IDBTransaction) => void): Promise<T | void> {
  return openDb().then(
    (db) =>
      new Promise<T | void>((resolve, reject) => {
        const t = db.transaction(stores, mode);
        t.oncomplete = () => resolve();
        t.onabort = () => reject(t.error);
        t.onerror = () => reject(t.error);
        fn(t);
      }),
  );
}

function reqResult<T>(req: IDBRequest<T>): Promise<T> {
  return new Promise((resolve, reject) => {
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(req.error);
  });
}

// ---------- songs ----------

export async function putSongs(songs: SongDetail[]): Promise<void> {
  if (songs.length === 0) return;
  await tx([STORE_SONGS], 'readwrite', (t) => {
    const store = t.objectStore(STORE_SONGS);
    for (const s of songs) store.put(s);
  });
}

export async function allSongs(): Promise<SongDetail[]> {
  const db = await openDb();
  return reqResult(db.transaction(STORE_SONGS, 'readonly').objectStore(STORE_SONGS).getAll() as IDBRequest<SongDetail[]>);
}

export async function getSong(id: string): Promise<SongDetail | undefined> {
  const db = await openDb();
  return reqResult(db.transaction(STORE_SONGS, 'readonly').objectStore(STORE_SONGS).get(id) as IDBRequest<SongDetail | undefined>);
}

/** Drop every cached song not in `keep` (server deletions converge on the next sync). */
export async function pruneSongs(keep: string[]): Promise<void> {
  const keepSet = new Set(keep);
  await tx([STORE_SONGS], 'readwrite', (t) => {
    const store = t.objectStore(STORE_SONGS);
    store.openCursor().onsuccess = (e) => {
      const cursor = (e.target as IDBRequest<IDBCursorWithValue | null>).result;
      if (!cursor) return;
      if (!keepSet.has(String(cursor.key))) cursor.delete();
      cursor.continue();
    };
  });
}

/**
 * MT-05: re-key pushed rows — the server issued new ids (restore push), so the cache row
 * moves from clientId to serverId. Rows absent from the store are skipped.
 */
export async function remapSongs(map: Record<string, string>): Promise<void> {
  const entries = Object.entries(map);
  if (entries.length === 0) return;
  await tx([STORE_SONGS], 'readwrite', (t) => {
    const store = t.objectStore(STORE_SONGS);
    for (const [from, to] of entries) {
      store.get(from).onsuccess = (e) => {
        const row = (e.target as IDBRequest<SongDetail | undefined>).result;
        if (!row) return;
        store.put({ ...row, id: to });
        store.delete(from);
      };
    }
  });
}

// ---------- playlists ----------

export async function putPlaylists(playlists: PlaylistDetail[]): Promise<void> {
  if (playlists.length === 0) return;
  await tx([STORE_PLAYLISTS], 'readwrite', (t) => {
    const store = t.objectStore(STORE_PLAYLISTS);
    for (const p of playlists) store.put(p);
  });
}

export async function allPlaylists(): Promise<PlaylistDetail[]> {
  const db = await openDb();
  return reqResult(db.transaction(STORE_PLAYLISTS, 'readonly').objectStore(STORE_PLAYLISTS).getAll() as IDBRequest<PlaylistDetail[]>);
}

export async function getPlaylist(id: string): Promise<PlaylistDetail | undefined> {
  const db = await openDb();
  return reqResult(db.transaction(STORE_PLAYLISTS, 'readonly').objectStore(STORE_PLAYLISTS).get(id) as IDBRequest<PlaylistDetail | undefined>);
}

export async function prunePlaylists(keep: string[]): Promise<void> {
  const keepSet = new Set(keep);
  await tx([STORE_PLAYLISTS], 'readwrite', (t) => {
    const store = t.objectStore(STORE_PLAYLISTS);
    store.openCursor().onsuccess = (e) => {
      const cursor = (e.target as IDBRequest<IDBCursorWithValue | null>).result;
      if (!cursor) return;
      if (!keepSet.has(String(cursor.key))) cursor.delete();
      cursor.continue();
    };
  });
}

/**
 * MT-05: re-key pushed playlists — the playlist id moves clientId → serverId, and the ids
 * inside its `songs` list follow the song push's mapping.
 */
export async function remapPlaylists(map: Record<string, string>, songMap: Record<string, string>): Promise<void> {
  const entries = Object.entries(map);
  if (entries.length === 0) return;
  await tx([STORE_PLAYLISTS], 'readwrite', (t) => {
    const store = t.objectStore(STORE_PLAYLISTS);
    for (const [from, to] of entries) {
      store.get(from).onsuccess = (e) => {
        const row = (e.target as IDBRequest<PlaylistDetail | undefined>).result;
        if (!row) return;
        store.put({
          ...row,
          id: to,
          songs: row.songs.map((s) => (songMap[s.id] ? { ...s, id: songMap[s.id] } : s)),
        });
        store.delete(from);
      };
    }
  });
}

// ---------- meta ----------

export async function setMeta<T>(key: string, value: T): Promise<void> {
  await tx([STORE_META], 'readwrite', (t) => {
    t.objectStore(STORE_META).put(value, key);
  });
}

export async function getMeta<T>(key: string): Promise<T | undefined> {
  const db = await openDb();
  return reqResult(db.transaction(STORE_META, 'readonly').objectStore(STORE_META).get(key) as IDBRequest<T | undefined>);
}

/** Well-known meta keys (kept typed so sync + readers agree). */
export interface OfflineMeta {
  lastSyncAt: string; // the server's SyncResponse.serverTime
  collections: Collection[];
  songCount: number;
}
