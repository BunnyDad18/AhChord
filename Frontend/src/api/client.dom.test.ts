import { afterEach, describe, expect, it, vi } from 'vitest';
import { apiSongGroups, apiSongs, apiTags } from './client';

/**
 * ST-112: the tag filter's wire contract — the server binds a REPEATED `tag`
 * key (string[] tag); a `tags=` key silently filters nothing. The endpoint tests
 * hand-write the URL, so this pins the client side: every list call must send
 * `tag=a&tag=b`, never `tags=`.
 */
function stubFetchCapturingUrl(): (n?: number) => string {
  const urls: string[] = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(async (url: string) => {
      urls.push(String(url));
      return { ok: true, status: 200, text: async () => '[]' } as Response;
    }),
  );
  return (n = 0) => urls[n];
}

afterEach(() => vi.unstubAllGlobals());

describe('tag filter wire format (ST-112)', () => {
  it('apiSongs sends repeated tag= keys, not tags=', async () => {
    const url = stubFetchCapturingUrl();
    await apiSongs('q', 1, 50, 'Matt Redman', 'Pop', 2020, ['Sunday', 'Stage']);
    expect(url()).toContain('tag=Sunday&tag=Stage');
    expect(url()).not.toContain('tags=');
    expect(url()).toContain('artist=Matt%20Redman');
  });

  it('apiSongGroups sends repeated tag= keys, not tags=', async () => {
    const url = stubFetchCapturingUrl();
    await apiSongGroups(undefined, 1, 50, undefined, undefined, undefined, ['Sunday', 'Kids']);
    expect(url()).toContain('tag=Sunday&tag=Kids');
    expect(url()).not.toContain('tags=');
    expect(url()).toContain('grouped=true');
  });

  it('empty tags are omitted entirely', async () => {
    const url = stubFetchCapturingUrl();
    await apiSongs(undefined, 1, 50, undefined, undefined, undefined, []);
    expect(url()).not.toContain('tag');
  });

  it('apiTags encodes kind and q', async () => {
    const url = stubFetchCapturingUrl();
    await apiTags('tag', 'pop rock');
    expect(url()).toContain('/tags?kind=tag&q=pop%20rock');
  });
});
