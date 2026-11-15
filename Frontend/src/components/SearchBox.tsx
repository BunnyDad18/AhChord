import { useEffect, useRef, useState } from 'react';
import { useNavigate } from 'react-router-dom';
import { apiSongs } from '../api/client';
import { offlineSongSearch } from '../offline/query';
import type { Song } from '../api/types';
import styles from './SearchBox.module.css';

/**
 * Global song search (ST-17): debounced (300ms) lookup of title/artist,
 * results dropdown, click → /songs/:id, "No songs match" empty state.
 * Closes on navigation, Esc, or outside click.
 */
export function SearchBox() {
  const [query, setQuery] = useState('');
  const [results, setResults] = useState<Song[] | null>(null);
  const [open, setOpen] = useState(false);
  const [searching, setSearching] = useState(false);
  const [active, setActive] = useState(-1);
  const rootRef = useRef<HTMLDivElement>(null);
  const searchRun = useRef(0); // ST-64: fetch race guard (see the search effect)
  const navigate = useNavigate();

  useEffect(() => {
    const q = query.trim();
    // ST-64: per-query run token — an in-flight apiSongs for an earlier query must not
    // overwrite current results (the debounce cancels the timer, not the request).
    const run = ++searchRun.current;
    let t: number;
    if (q.length < 2) {
      // Clear the dropdown once (deferred so state updates happen in a callback).
      t = window.setTimeout(() => {
        setResults(null);
        setOpen(false);
        setSearching(false);
      }, 0);
    } else {
      t = window.setTimeout(() => {
        setSearching(true);
        apiSongs(q, 1, 8)
          .then((songs) => {
            if (run !== searchRun.current) return;
            setResults(songs);
            setOpen(true);
            setActive(-1);
          })
          .catch(async () => {
            // MT-04: offline → search the cached library.
            const cached = await offlineSongSearch(q, 8).catch(() => null);
            if (run !== searchRun.current) return;
            setResults(cached);
            setOpen(!!cached && cached.length > 0);
            setActive(-1);
          })
          .finally(() => {
            if (run === searchRun.current) setSearching(false);
          });
      }, 300);
    }
    return () => window.clearTimeout(t);
  }, [query]);

  // Close on outside click.
  useEffect(() => {
    function onDown(e: MouseEvent) {
      if (rootRef.current && !rootRef.current.contains(e.target as Node)) {
        setOpen(false);
      }
    }
    document.addEventListener('mousedown', onDown);
    return () => document.removeEventListener('mousedown', onDown);
  }, []);

  function go(song: Song) {
    setOpen(false);
    setQuery('');
    setResults(null);
    navigate(`/songs/${song.id}`);
  }

  return (
    <div className={styles.root} ref={rootRef} role="search">
      <input
        className={styles.input}
        type="search"
        placeholder="Search songs…"
        aria-label="Search songs"
        value={query}
        onChange={(e) => setQuery(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === 'Escape') {
            setOpen(false);
            setQuery('');
          } else if (e.key === 'ArrowDown' && results && results.length > 0) {
            e.preventDefault();
            setActive((a) => Math.min(a + 1, results.length - 1));
          } else if (e.key === 'ArrowUp' && results) {
            e.preventDefault();
            setActive((a) => Math.max(a - 1, -1));
          } else if (e.key === 'Enter' && results && active >= 0) {
            go(results[active]);
          } else if (e.key === 'Enter' && results && results.length > 0) {
            go(results[0]);
          }
        }}
      />
      {searching && (
        <span className={styles.spinner} aria-label="Searching" />
      )}
      {open && results !== null && (
        <ul className={styles.results} role="listbox" aria-label="Search results">
          {results.length === 0 && <li className={styles.empty}>No songs match</li>}
          {results.map((song, i) => (
            <li key={song.id}>
              <button
                type="button"
                className={`${styles.result} ${i === active ? styles.active : ''}`}
                role="option"
                aria-selected={i === active}
                onMouseEnter={() => setActive(i)}
                onClick={() => go(song)}
              >
                <span className={styles.title}>{song.title}</span>
                {song.artist && <span className={styles.artist}>{song.artist}</span>}
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
