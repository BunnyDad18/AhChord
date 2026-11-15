/**
 * MT-04: offline reads from the IndexedDB cache (db.ts).
 *
 * The pure cores (grouping, facets, chart projection) are exported separately and unit-tested;
 * the async wrappers read the cache and call them with the same semantics the server endpoints
 * implement (ST-35 grouping, facets, playlist ordering), so pages behave the same offline.
 */

import type {
  Collection,
  Playlist,
  PlaylistDetail,
  RecentSong,
  Song,
  SongDetail,
  SongDetailResponse,
  SongFacets,
  SongGroup,
  TagRow,
} from '../api/types';
import { parseChordPro } from '../utils/chordpro';
import { transposeParsed } from '../utils/transpose';
import { allPlaylists, allSongs, getMeta, getPlaylist, getSong } from './db';

// ---------- chart projection (GET /api/songs/{id}?capo=N offline mirror) ----------

/**
 * Rebuild the detail response from cached ChordPro text: parse + transpose by
 * session transpose − capoFret — the server's read-time projection (ST-08/ST-51 #8,
 * ST-77 bug report #116: the capo moves the SHAPES down and keeps the key; the
 * transpose moves both).
 */
export function offlineSongFromDetail(song: SongDetail, transpose: number): SongDetailResponse {
  const parsed = parseChordPro(song.chordProText);
  const shift = transpose - (song.capoFret ?? 0);
  if (shift !== 0) transposeParsed(parsed, shift);
  // ParsedSongLite is structurally the ParsedSong the renderer consumes (utils/chordpro.ts
  // is the kept-in-sync mirror of the C# parser).
  return { song, parsed };
}

export async function offlineSongDetail(id: string, transpose: number): Promise<SongDetailResponse | null> {
  const song = await getSong(id);
  return song ? offlineSongFromDetail(song, transpose) : null;
}

/** GET /api/songs/{id}/versions offline: cached songs with the same title+artist (case-insensitive). */
export async function offlineVersions(id: string): Promise<Song[] | null> {
  const target = await getSong(id);
  if (!target) return null;
  const key = (s: Song) => `${s.title.trim().toLowerCase()}\u0000${(s.artist ?? '').trim().toLowerCase()}`;
  const versions = (await allSongs())
    .filter((s) => key(s) === key(target))
    .sort((a, b) => a.createdAt.localeCompare(b.createdAt) || a.id.localeCompare(b.id));
  return versions.length >= 2 ? versions.map(toSong) : null;
}

// ---------- songs list (GET /api/songs?grouped=true offline mirror) ----------

const strip = (s: string) => s.trim().toLowerCase();

function matches(hay: string | null, needle: string): boolean {
  return hay !== null && strip(hay).includes(strip(needle));
}

/** ST-106: the tag lists, with the legacy flat cache as fallback (pre-backfill rows). */
const artistsOf = (s: SongDetail | Song) =>
  s.artists?.length ? s.artists : s.artist ? [s.artist] : [];
const genresOf = (s: SongDetail | Song) =>
  s.genres?.length ? s.genres : s.genre ? [s.genre] : [];
const tagsOf = (s: SongDetail | Song) => s.tags ?? [];

/** ST-106: exact tag-name match, case-insensitive (the server matches NormalizedName). */
const nameIs = (names: string[], needle: string) => names.some((n) => strip(n) === strip(needle));

function filterSongs(
  songs: SongDetail[],
  f: { search?: string; artist?: string; genre?: string; decade?: number; tags?: string[] },
): SongDetail[] {
  return songs.filter((s) => {
    // (capture the narrowed search — property narrowing does not survive the closure)
    const q = f.search?.trim();
    if (
      q &&
      !(
        matches(s.title, q) ||
        matches(s.artist, q) ||
        [...artistsOf(s), ...genresOf(s), ...tagsOf(s)].some((n) => matches(n, q))
      )
    )
      return false;
    // ST-106: artist/genre/tag are exact tag-name matches; tags AND-narrow.
    if (f.artist && !nameIs(artistsOf(s), f.artist)) return false;
    if (f.genre && !nameIs(genresOf(s), f.genre)) return false;
    if (f.tags?.length && !f.tags.every((t) => nameIs(tagsOf(s), t))) return false;
    if (f.decade) {
      const start = f.decade - (f.decade % 10);
      if (s.year == null || s.year < start || s.year >= start + 10) return false;
    }
    return true;
  });
}

/**
 * Group by title only (case-insensitive — ST-106: multi-artist versions of one song
 * collapse together), versions collapsed — mirrors SongEndpoints grouped mode:
 * latest = newest CreatedAt (ties by id), ids oldest-first, groups sorted by title,
 * case-insensitive. `pendingProposals` is 0 offline (review is online).
 */
export function buildGroupsFromSongs(songs: SongDetail[]): SongGroup[] {
  const byKey = new Map<string, SongDetail[]>();
  for (const s of songs) {
    const key = strip(s.title);
    const list = byKey.get(key);
    if (list) list.push(s);
    else byKey.set(key, [s]);
  }
  const groups: SongGroup[] = [];
  for (const rows of byKey.values()) {
    const sorted = [...rows].sort((a, b) => a.createdAt.localeCompare(b.createdAt) || a.id.localeCompare(b.id));
    const latest = sorted[sorted.length - 1];
    groups.push({
      title: latest.title,
      artist: latest.artist,
      versionCount: sorted.length,
      latest: toSong(latest),
      ids: sorted.map((s) => s.id),
      pendingProposals: 0,
    });
  }
  groups.sort((a, b) => strip(a.title).localeCompare(strip(b.title)));
  return groups;
}

export async function offlineSongGroups(
  search?: string,
  page = 1,
  pageSize = 20,
  artist?: string,
  genre?: string,
  decade?: number,
  tags?: string[],
): Promise<SongGroup[]> {
  const groups = buildGroupsFromSongs(
    filterSongs(await allSongs(), { search, artist, genre, decade, tags }),
  );
  return groups.slice((page - 1) * pageSize, page * pageSize);
}

/**
 * GET /api/songs/facets offline: distinct artists/genres (from the tag lists, the
 * flat cache as fallback), free tags, decades. ST-106: total counts title-only groups.
 */
export async function offlineSongFacets(): Promise<SongFacets> {
  const songs = await allSongs();
  const distinct = (values: Array<string | null>) => {
    const seen = new Map<string, string>();
    for (const v of values) {
      const t = v?.trim();
      if (t && !seen.has(strip(t))) seen.set(strip(t), t);
    }
    return [...seen.values()].sort((a, b) => strip(a).localeCompare(strip(b)));
  };
  const decades = [
    ...new Set(songs.filter((s) => s.year != null).map((s) => Math.floor(s.year! / 10) * 10)),
  ].sort((a, b) => a - b);
  return {
    artists: distinct(songs.flatMap(artistsOf)),
    genres: distinct(songs.flatMap(genresOf)),
    decades,
    total: buildGroupsFromSongs(songs).length,
    tags: distinct(songs.flatMap(tagsOf)),
  };
}

// ---------- playlists ----------

export async function offlinePlaylists(search?: string, tag?: string): Promise<Playlist[]> {
  const pls = await allPlaylists();
  const q = search?.trim();
  const filtered = pls
    .filter((p) => (q ? matches(p.name, q) || matches(p.description, q) : true))
    // ST-106: ?tag= — exact tag-name match (case-insensitive) on the setlist's tags.
    .filter((p) => (tag ? nameIs(p.tags ?? [], tag) : true));
  return filtered.map(toPlaylist).sort((a, b) => strip(a.name).localeCompare(strip(b.name)));
}

export async function offlinePlaylist(id: string): Promise<PlaylistDetail | null> {
  return (await getPlaylist(id)) ?? null;
}

// ---------- tag browser (GET /api/tags?kind=tag offline mirror) ----------

/**
 * ST-106: the tags seen on cached songs/playlists, with cache-side counts.
 * The tag table itself is not cached, so offline this is the best available mirror.
 */
export async function offlineTags(): Promise<TagRow[]> {
  const [songs, pls] = await Promise.all([allSongs(), allPlaylists()]);
  const byName = new Map<string, TagRow>();
  for (const s of songs)
    for (const t of tagsOf(s)) {
      const row = byName.get(strip(t)) ?? { kind: 'tag', name: t, songCount: 0, playlistCount: 0 };
      row.songCount++;
      byName.set(strip(t), row);
    }
  for (const p of pls)
    for (const t of p.tags ?? []) {
      const row = byName.get(strip(t)) ?? { kind: 'tag', name: t, songCount: 0, playlistCount: 0 };
      row.playlistCount++;
      byName.set(strip(t), row);
    }
  return [...byName.values()];
}

// ---------- collections / dashboard ----------

export async function offlineCollections(): Promise<Collection[]> {
  return (await getMeta<Collection[]>('collections')) ?? [];
}

export async function offlineSongCount(): Promise<number> {
  return (await getMeta<number>('songCount')) ?? (await allSongs()).length;
}

// ---------- recent views (GET /api/songs/recent offline mirror) ----------

const VIEWED_KEY = 'ahchord.viewed';
const VIEWED_MAX = 50;

interface ViewedEntry {
  id: string;
  at: string;
}

function readViewed(): ViewedEntry[] {
  try {
    const raw = localStorage.getItem(VIEWED_KEY);
    const list = raw ? (JSON.parse(raw) as ViewedEntry[]) : [];
    return Array.isArray(list) ? list : [];
  } catch {
    return [];
  }
}

/** Record a view locally (the server keeps its own history; this drives Recent offline). */
export function recordView(songId: string): void {
  const at = new Date().toISOString();
  const list = [{ id: songId, at }, ...readViewed().filter((v) => v.id !== songId)].slice(0, VIEWED_MAX);
  try {
    localStorage.setItem(VIEWED_KEY, JSON.stringify(list));
  } catch {
    /* storage is best-effort */
  }
}

export async function offlineRecent(limit = 5): Promise<RecentSong[]> {
  const viewed = readViewed();
  const out: RecentSong[] = [];
  for (const v of viewed) {
    if (out.length >= limit) break;
    const s = await getSong(v.id);
    if (s) out.push({ ...toSong(s), viewedAt: v.at });
  }
  return out;
}

// ---------- random (GET /api/songs/random offline mirror) ----------

export async function offlineRandomSong(playlistId?: string): Promise<Song | null> {
  let pool: Song[];
  if (playlistId) {
    const pl = await getPlaylist(playlistId);
    pool = pl ? pl.songs : [];
  } else {
    pool = (await allSongs()).map(toSong);
  }
  if (pool.length === 0) return null;
  return pool[Math.floor(Math.random() * pool.length)];
}

// ---------- search box ----------

export async function offlineSongSearch(q: string, limit = 8): Promise<Song[]> {
  const songs = filterSongs(await allSongs(), { search: q })
    .sort((a, b) => strip(a.title).localeCompare(strip(b.title)))
    .slice(0, limit);
  return songs.map(toSong);
}

// ---------- projections (SongDetail carries chordProText; list shapes must not) ----------

function toSong(s: SongDetail): Song {
  const { chordProText: _c, sourceUrl: _u, ...song } = s;
  return song;
}

function toPlaylist(p: PlaylistDetail): Playlist {
  const { songs: _s, ...playlist } = p;
  return playlist;
}
