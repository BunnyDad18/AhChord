import { useEffect, useState } from 'react';
import {
  apiMusicBrainzSearch,
  apiSongsterrSearch,
  apiConvertStart,
  apiWishlist,
  apiWishlistCreate,
  apiWishlistDelete,
  apiWishlistUpdate,
  type SearchScope,
} from '../api/client';
import type { MusicBrainzResult, SongsterrResult, WishlistItem } from '../api/types';
import { useImportQueueStore } from '../stores/importQueue';
import { useToastStore } from '../stores/toast';
import ui from './ui.module.css';
import styles from './SearchTab.module.css';

/** localStorage key linking an LLM draft to its wishlist item (ST-44). */
export function wishlistDraftKey(draftId: string): string {
  return `ahchord-wishlist-draft:${draftId}`;
}

function songsterrUrl(songId: number): string {
  return `https://www.songsterr.com/a/wa/view?r=${songId}`;
}

/** First line of the pasted text, capped at 80 chars (queue-row preview). */
function firstLine(t: string): string | null {
  const line = t.split('\n').find((l) => l.trim()) ?? '';
  return line.length > 80 ? `${line.slice(0, 79)}…` : line;
}

/** ST-45: normalized title+artist key used to detect songs already on the wishlist. */
function songKey(title: string, artist?: string | null): string {
  const norm = (s: string) => s.trim().toLowerCase().replace(/\s+/g, ' ');
  return `${norm(title)}|${artist ? norm(artist) : ''}`;
}

function StatusBadge({ status }: { status: WishlistItem['status'] }) {
  const label = status === 'waiting' ? 'waiting' : status === 'found' ? 'converting' : 'converted ✓';
  return <span className={`${ui.badge} ${status !== 'waiting' ? ui.badgeAccent : ''}`}>{label}</span>;
}

/**
 * ST-44: the Search tab on /import — find a song (Songsterr chord sources + MusicBrainz
 * metadata), keep it on the per-user wishlist, and paste chord text from anywhere to start
 * an LLM conversion. When the resulting draft is saved as a song, Import's save flow marks
 * the wishlist item converted (via the localStorage link in `wishlistDraftKey`).
 */
export function SearchTab() {
  const pushToast = useToastStore((s) => s.push);
  const addDraft = useImportQueueStore((s) => s.addDraft);

  // Search.
  const [query, setQuery] = useState('');
  const [scope, setScope] = useState<SearchScope>('everything');
  const [searching, setSearching] = useState(false);
  const [stResults, setStResults] = useState<SongsterrResult[] | null>(null);
  const [mbResults, setMbResults] = useState<MusicBrainzResult[] | null>(null);
  const [searchError, setSearchError] = useState<string | null>(null);

  // Wishlist.
  const [wishlist, setWishlist] = useState<WishlistItem[]>([]);
  // ST-45: keys of songs already on the wishlist — search rows show "on wishlist" instead of a button.
  const wishlistKeys = new Set(wishlist.map((i) => songKey(i.title, i.artist)));
  const [wlLoading, setWlLoading] = useState(true);
  const [adding, setAdding] = useState(false);

  // Paste box (tied to one wishlist item).
  const [pasteTarget, setPasteTarget] = useState<WishlistItem | null>(null);
  const [pasteText, setPasteText] = useState('');
  const [pasting, setPasting] = useState(false);

  useEffect(() => {
    apiWishlist()
      .then(setWishlist)
      .catch(() => pushToast('Could not load the wishlist.', 'error'))
      .finally(() => setWlLoading(false));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  async function doSearch() {
    const q = query.trim();
    if (q.length < 2 || searching) return;
    setSearching(true);
    setSearchError(null);
    // ST-45: scope routes which source(s) run — songs → Songsterr only, artists/albums → MusicBrainz only.
    const useSt = scope === 'everything' || scope === 'songs';
    const useMb = scope !== 'songs';
    try {
      const [st, mb] = await Promise.allSettled([
        useSt ? apiSongsterrSearch(q, 30) : Promise.reject(new Error('skipped')),
        useMb ? apiMusicBrainzSearch(q, 20, scope === 'artists' ? 'artists' : undefined) : Promise.reject(new Error('skipped')),
      ]);
      setStResults(useSt && st.status === 'fulfilled' ? st.value.results : null);
      setMbResults(useMb && mb.status === 'fulfilled' ? mb.value.results : null);
      if ((useSt && st.status === 'rejected') || (useMb && mb.status === 'rejected')) {
        setSearchError('Song search is unavailable right now — try again in a moment.');
      }
    } finally {
      setSearching(false);
    }
  }

  async function addToWishlist(title: string, artist: string | null, sourceUrl?: string) {
    const t = title.trim();
    if (!t || adding) return;
    setAdding(true);
    try {
      const item = await apiWishlistCreate({ title: t, artist: artist?.trim() || null, sourceUrl });
      setWishlist((w) => [item, ...w]);
      pushToast(`“${t}” added to the wishlist.`);
    } catch (e: unknown) {
      pushToast(e instanceof Error ? e.message : 'Could not add to the wishlist.', 'error');
    } finally {
      setAdding(false);
    }
  }

  async function removeItem(item: WishlistItem) {
    try {
      await apiWishlistDelete(item.id);
      setWishlist((w) => w.filter((x) => x.id !== item.id));
      if (pasteTarget?.id === item.id) closePasteBox();
    } catch (e: unknown) {
      pushToast(e instanceof Error ? e.message : 'Could not remove the item.', 'error');
    }
  }

  function openPasteBox(item: WishlistItem) {
    setPasteTarget(item);
    setPasteText('');
  }

  function closePasteBox() {
    setPasteTarget(null);
    setPasteText('');
  }

  // Paste box -> LLM conversion queue + wishlist item flips to "found".
  async function startConversion() {
    const content = pasteText.trim();
    if (!pasteTarget || !content || pasting) return;
    setPasting(true);
    try {
      const { id } = await apiConvertStart({ content });
      localStorage.setItem(wishlistDraftKey(id), pasteTarget.id);
      addDraft({
        id,
        status: 'queued',
        inputUrl: null,
        inputPreview: firstLine(content),
        createdAt: new Date().toISOString(),
      });
      // Best-effort status flip — a failure here must not mask the successful enqueue.
      try {
        const updated = await apiWishlistUpdate(pasteTarget.id, { status: 'found' });
        setWishlist((w) => w.map((x) => (x.id === updated.id ? updated : x)));
      } catch {
        pushToast('The conversion is queued, but the wishlist status could not be updated.', 'error');
      }
      pushToast(`Conversion started for “${pasteTarget.title}” — watch the queue.`);
      closePasteBox();
    } catch (e: unknown) {
      pushToast(e instanceof Error ? e.message : 'Could not start the conversion.', 'error');
    } finally {
      setPasting(false);
    }
  }

  return (
    <div className={styles.col}>
      {/* Find a song */}
      <div className={ui.card}>
        <h2>Find a song</h2>
        <p className={ui.muted}>
          Search chord sources and music metadata. Add what you need to the wishlist below,
          then paste the chord text from anywhere when you have it.
        </p>
        <div className={styles.searchRow}>
          <input
            className={styles.input}
            type="search"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === 'Enter') void doSearch();
            }}
            placeholder='Song title, artist… e.g. "Creep Radiohead"'
            aria-label="Search songs"
          />
          <select
            className={styles.scopeSelect}
            value={scope}
            onChange={(e) => setScope(e.target.value as SearchScope)}
            aria-label="Search scope"
          >
            <option value="everything">Everything</option>
            <option value="songs">Songs</option>
            <option value="artists">Artists</option>
            <option value="albums">Albums</option>
          </select>
          <button
            type="button"
            className={ui.btn}
            onClick={() => void doSearch()}
            disabled={query.trim().length < 2 || searching}
          >
            {searching ? 'Searching…' : 'Search'}
          </button>
        </div>
        {searchError && (
          <p className={ui.errorBanner} role="alert">
            {searchError}
          </p>
        )}

        {stResults !== null && (
          <>
            <h3 className={styles.groupTitle}>Chord sources — Songsterr</h3>
            {stResults.length === 0 ? (
              <p className={ui.muted}>No songs found on Songsterr.</p>
            ) : (
              <ul className={styles.list}>
                {stResults.map((r) => (
                  <li key={`st-${r.songId}`} className={styles.row}>
                    <div className={styles.rowMain}>
                      <span className={styles.rowTitle}>{r.title}</span>
                      {r.artist && <span className={ui.muted}> — {r.artist}</span>}
                      {r.hasChords ? (
                        <span className={`${ui.badge} ${ui.badgeAccent}`}>chords</span>
                      ) : (
                        <span className={ui.badge}>no chords</span>
                      )}
                    </div>
                    <div className={styles.rowActions}>
                      <a href={songsterrUrl(r.songId)} target="_blank" rel="noreferrer" className={ui.muted}>
                        Open ↗
                      </a>
                      {wishlistKeys.has(songKey(r.title, r.artist)) ? (
                        <span className={`${ui.badge} ${ui.badgeAccent}`}>on wishlist ✓</span>
                      ) : (
                        <button
                          type="button"
                          className={`${ui.btn} ${ui.small}`}
                          disabled={adding}
                          onClick={() => void addToWishlist(r.title, r.artist, songsterrUrl(r.songId))}
                        >
                          + Wishlist
                        </button>
                      )}
                    </div>
                  </li>
                ))}
              </ul>
            )}
          </>
        )}

        {mbResults !== null && (
          <>
            <h3 className={styles.groupTitle}>Metadata — MusicBrainz</h3>
            {mbResults.length === 0 ? (
              <p className={ui.muted}>No matching recordings in MusicBrainz.</p>
            ) : (
              <ul className={styles.list}>
                {mbResults.map((r) => (
                  <li key={`mb-${r.mbid}`} className={styles.row}>
                    <div className={styles.rowMain}>
                      <span className={styles.rowTitle}>{r.title}</span>
                      {r.artist && <span className={ui.muted}> — {r.artist}</span>}
                      {r.year !== null && <span className={ui.muted}>({r.year})</span>}
                    </div>
                    <div className={styles.rowActions}>
                      {wishlistKeys.has(songKey(r.title, r.artist)) ? (
                        <span className={`${ui.badge} ${ui.badgeAccent}`}>on wishlist ✓</span>
                      ) : (
                        <button
                          type="button"
                          className={`${ui.btn} ${ui.small}`}
                          disabled={adding}
                          onClick={() => void addToWishlist(r.title, r.artist)}
                        >
                          + Wishlist
                        </button>
                      )}
                    </div>
                  </li>
                ))}
              </ul>
            )}
          </>
        )}
      </div>

      {/* Wishlist */}
      <div className={ui.card}>
        <h2>Wishlist</h2>
        <p className={ui.muted}>
          Songs you want chords for. Paste the chord text when you have it — conversion runs on
          this server’s LLM and lands in the queue.
        </p>
        {wlLoading ? (
          <p className={ui.muted}>Loading…</p>
        ) : wishlist.length === 0 ? (
          <p className={ui.muted}>Nothing here yet — search above and add a song.</p>
        ) : (
          <ul className={styles.list}>
            {wishlist.map((item) => (
              <li key={item.id} className={styles.row}>
                <div className={styles.rowMain}>
                  <span className={styles.rowTitle}>{item.title}</span>
                  {item.artist && <span className={ui.muted}> — {item.artist}</span>}
                  <StatusBadge status={item.status} />
                  {item.sourceUrl && (
                    <a href={item.sourceUrl} target="_blank" rel="noreferrer" className={ui.muted}>
                      source ↗
                    </a>
                  )}
                </div>
                {item.status !== 'converted' && (
                  <div className={styles.rowActions}>
                    <button type="button" className={`${ui.btn} ${ui.small}`} onClick={() => openPasteBox(item)}>
                      Paste chords
                    </button>
                    <button
                      type="button"
                      className={`${ui.btn} ${ui.small} ${ui.danger}`}
                      onClick={() => void removeItem(item)}
                    >
                      Remove
                    </button>
                  </div>
                )}
              </li>
            ))}
          </ul>
        )}

        {pasteTarget && (
          <div className={styles.pasteBox}>
            <label className={ui.muted} htmlFor="search-paste">
              Paste the chord text for “{pasteTarget.title}” from anywhere:
            </label>
            <textarea
              id="search-paste"
              className={styles.pasteArea}
              value={pasteText}
              onChange={(e) => setPasteText(e.target.value)}
              placeholder="Lyrics + chord lines…"
            />
            <div className={styles.searchRow}>
              <button
                type="button"
                className={`${ui.btn} ${ui.primary}`}
                disabled={!pasteText.trim() || pasting}
                onClick={() => void startConversion()}
              >
                {pasting ? 'Starting…' : 'Start conversion'}
              </button>
              <button type="button" className={ui.btn} onClick={closePasteBox}>
                Cancel
              </button>
            </div>
          </div>
        )}
      </div>
    </div>
  );
}
