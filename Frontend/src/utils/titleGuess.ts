// ST-50: infer title/artist clues from a chart filename, and match MusicBrainz search results
// against a candidate string. Pure functions — the batch import panel (Import.tsx) uses them to
// fill in missing {title:}/{artist:} header directives before importing.

/** Lowercase alphanumeric tokens: "Hotel_California" → ["hotel", "california"]. */
export function tokens(s: string): string[] {
  return s
    .toLowerCase()
    .split(/[^a-z0-9]+/)
    .filter(Boolean);
}

export interface FilenameGuess {
  title?: string;
  artist?: string;
}

/**
 * Heuristics, first match wins:
 *  - "Title - Artist" → split on the first " - "
 *  - "Title (Artist)" / "Title [Artist]" → trailing parenthesized part is the artist
 *  - otherwise the whole stem is a single title candidate (underscores read as spaces).
 */
export function parseFilename(name: string): FilenameGuess {
  const i = name.lastIndexOf('.');
  let stem = (i >= 0 ? name.slice(0, i) : name).trim();
  if (!stem) return {};

  // "Title - Artist"
  const dash = stem.indexOf(' - ');
  if (dash > 0) {
    const title = stem.slice(0, dash).trim();
    const artist = stem.slice(dash + 3).trim();
    if (title && artist) return { title, artist };
  }

  // "Title (Artist)" / "Title [Artist]" — trailing parens only.
  const m = stem.match(/^(.*?)\s*[([](.+)[)\]]$/);
  if (m && m[1].trim() && m[2].trim()) {
    return { title: m[1].trim(), artist: m[2].trim() };
  }

  // Single candidate — underscores read as spaces.
  const single = stem.replace(/_+/g, ' ').replace(/\s+/g, ' ').trim();
  return single ? { title: single } : {};
}

export interface MusicBrainzMatch {
  title: string;
  artist?: string;
}

/**
 * Conservative match against a MusicBrainz result list (best first): the candidate's tokens must
 * be ≥75% covered by the result's title+artist tokens, and at least one token must hit the title
 * itself (an artist-only overlap can't win). Returns null when nothing is confident enough —
 * the row then imports with just its title.
 */
export function matchMusicBrainz(
  candidate: string,
  results: Array<{ title: string; artist?: string | null }>,
): MusicBrainzMatch | null {
  const cand = tokens(candidate);
  if (cand.length === 0) return null;
  for (const r of results) {
    const tTok = new Set(tokens(r.title));
    const aTok = new Set(tokens(r.artist ?? ''));
    const covered = cand.filter((t) => tTok.has(t) || aTok.has(t)).length;
    if (covered < Math.ceil(cand.length * 0.75)) continue;
    if (!cand.some((t) => tTok.has(t))) continue;
    return { title: r.title, artist: r.artist ?? undefined };
  }
  return null;
}
