import { describe, expect, it } from 'vitest';
import type { PlaylistDetail, Song, SongDetail } from '../api/types';
import {
  chunk,
  localOnlyPlaylists,
  localOnlySongs,
  pushOutcomes,
  toPushPlaylist,
  toPushSong,
} from './push';

// MT-05: the restore-push policy — what a device uploads, and what pruning must spare.
// An uploaded row is the only copy; a row whose push failed must stay for the retry.

const song = (over: Partial<SongDetail> & { id: string; title: string }): SongDetail => ({
  artist: null,
  album: null,
  year: null,
  key: null,
  genre: null,
  tempo: null,
  capoFret: 0,
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-01T00:00:00Z',
  chordProText: '[C] text',
  sourceUrl: null,
  ...over,
});

const songRow = (id: string): Song => ({
  id,
  title: `Song ${id}`,
  artist: null,
  album: null,
  year: null,
  key: null,
  genre: null,
  tempo: null,
  capoFret: 0,
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-01T00:00:00Z',
});

const playlist = (over: Partial<PlaylistDetail> & { id: string; name: string }): PlaylistDetail => ({
  description: null,
  collectionId: null,
  isSetlist: false,
  gigDate: null,
  songCount: 0,
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-01T00:00:00Z',
  songs: [],
  ...over,
});

describe('localOnlySongs / localOnlyPlaylists', () => {
  it('uploads cached rows the server has never seen; never rows the server has', () => {
    const cached = [song({ id: 'a', title: 'Alpha' }), song({ id: 'b', title: 'Bravo' })];
    expect(localOnlySongs(cached, ['b']).map((s) => s.id)).toEqual(['a']);
    expect(localOnlySongs(cached, [])).toHaveLength(2);
    expect(localOnlySongs(cached, ['a', 'b'])).toHaveLength(0);
  });

  it('skips rows with no ChordPro text — the server cannot restore them', () => {
    const cached = [song({ id: 'a', title: 'Alpha', chordProText: '' })];
    expect(localOnlySongs(cached, [])).toHaveLength(0);
  });

  it('playlists prune the same way', () => {
    const cached = [playlist({ id: 'p1', name: 'Set' }), playlist({ id: 'p2', name: 'Other' })];
    expect(localOnlyPlaylists(cached, ['p2']).map((p) => p.id)).toEqual(['p1']);
  });
});

describe('payload builders', () => {
  it('toPushSong carries the full cache row under its clientId', () => {
    const s = song({ id: 'a', title: 'Alpha', artist: 'Queen', key: 'G', tempo: 120, capoFret: 2 });
    const push = toPushSong(s);
    expect(push.clientId).toBe('a');
    expect(push.title).toBe('Alpha');
    expect(push.artist).toBe('Queen');
    expect(push.key).toBe('G');
    expect(push.tempo).toBe(120);
    expect(push.capoFret).toBe(2);
    expect(push.chordProText).toBe('[C] text');
  });

  it('toPushPlaylist remaps pushed songs to their server ids, keeps the rest', () => {
    const p = playlist({ id: 'pl', name: 'Set', songs: [songRow('a'), songRow('b'), songRow('c')] });
    expect(toPushPlaylist(p, { b: 'B' }).songClientIds).toEqual(['a', 'B', 'c']);
  });

  it('chunk splits per the server caps', () => {
    expect(chunk([1, 2, 3, 4, 5], 2)).toEqual([[1, 2], [3, 4], [5]]);
    expect(chunk([], 100)).toEqual([]);
  });
});

describe('pushOutcomes (the pruning safety net)', () => {
  it('landed rows contribute the SERVER id; failed rows keep the CLIENT id', () => {
    const { keep, map } = pushOutcomes([
      { clientId: 'a', id: 'A', error: null },
      { clientId: 'b', id: '00000000-0000-0000-0000-000000000000', error: 'not-chordpro' },
    ]);
    // The remapped row is keyed by A — pruning must spare A, not 'a'.
    expect(keep).toEqual(['A', 'b']);
    expect(map).toEqual({ a: 'A' });
  });
});
