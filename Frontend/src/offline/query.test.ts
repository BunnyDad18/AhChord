import { describe, expect, it } from 'vitest';
import type { SongDetail } from '../api/types';
import { buildGroupsFromSongs, offlineSongFromDetail } from './query';

// MT-04: the pure offline cores — grouping mirrors the server's grouped list, and the
// chart projection mirrors GET /api/songs/{id}?capo=N (raw parse — the capo never moves the shapes).

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

describe('buildGroupsFromSongs', () => {
  it('collapses title+artist case-insensitively; latest = newest, ids oldest-first', () => {
    const songs = [
      song({ id: 'b', title: 'Alpha', artist: 'Queen', createdAt: '2026-05-01T00:00:00Z' }),
      song({ id: 'a', title: 'alpha ', artist: ' queen ', createdAt: '2026-02-01T00:00:00Z' }),
      song({ id: 'c', title: 'Bravo', createdAt: '2026-01-01T00:00:00Z' }),
    ];
    const groups = buildGroupsFromSongs(songs);
    expect(groups).toHaveLength(2);
    expect(groups[0].title).toBe('Alpha');
    expect(groups[0].versionCount).toBe(2);
    expect(groups[0].latest.id).toBe('b');
    expect(groups[0].ids).toEqual(['a', 'b']);
    expect(groups[0].pendingProposals).toBe(0);
    // Song detail (chordProText) never leaks into the list shape.
    expect(groups[0].latest).not.toHaveProperty('chordProText');
  });

  it('groups by title only — multi-artist versions collapse (ST-106); sorted by title', () => {
    const groups = buildGroupsFromSongs([
      song({ id: '1', title: 'zebra' }),
      song({ id: '2', title: 'Apple', artist: 'b' }),
      song({ id: '3', title: 'Apple', artist: 'A' }),
    ]);
    expect(groups.map((g) => `${g.title}/${g.versionCount}`)).toEqual(['Apple/2', 'zebra/1']);
  });
});

describe('offlineSongFromDetail (server projection mirror)', () => {
  it('moves the shapes by transpose − capo, keeping the key (ST-77 bug report #116)', () => {
    const s = song({ id: 'x', title: 'X', capoFret: 2, chordProText: '[C] walk [Am] home' });
    const { parsed } = offlineSongFromDetail(s, 1); // shift = 1 − 2 = −1: grips move down, key stays put
    const chords = parsed.sections[0].lines[0].chords.map((c) => c.name);
    expect(chords).toEqual(['B', 'G#m']);
  });

  it('sections + lines come out shaped for the renderer', () => {
    const s = song({ id: 'y', title: 'Y', chordProText: '{c: Verse}\n[G] play\n\n{chorus}\n[D] sing' });
    const { parsed } = offlineSongFromDetail(s, 0);
    expect(parsed.sections.map((sec) => sec.type)).toEqual(['other', 'chorus']);
    expect(parsed.sections[1].lines[0].text).toBe('[D] sing');
  });
});
