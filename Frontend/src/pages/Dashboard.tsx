import { useCallback, useEffect, useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import {
  apiCollections,
  apiPlaylists,
  apiSongCount,
  apiSongRandom,
  apiSongsRecent,
} from '../api/client';
import type { Collection, Playlist, RecentSong } from '../api/types';
import { isNetworkError } from '../offline/fallback';
import {
  offlineCollections,
  offlinePlaylists,
  offlineRandomSong,
  offlineRecent,
  offlineSongCount,
} from '../offline/query';
import { useToastStore } from '../stores/toast';
import { PlayIcon, ShuffleIcon } from '../components/Icons';
import { readLastPlay } from '../utils/lastPlay';
import { gigLabel, gigUpcoming } from '../utils/gigDate';
import type { LastPlay } from '../utils/lastPlay';
import ui from '../components/ui.module.css';
import styles from './Dashboard.module.css';

interface DashData {
  songsExist: boolean;
  songCount: number;
  recent: RecentSong[];
  collections: Collection[];
  playlists: Playlist[];
}

/**
 * Dashboard (ST-17; ST-35 C1 columns; reworked ST-75): a "Jump back in" hero
 * for what this device played last (▶ Play goes straight to the player), a
 * quiet library-count line, and three panels — Recently played · Playlists ·
 * Collections. The random-song action rides in the hero.
 */
export default function Dashboard() {
  const [data, setData] = useState<DashData | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [rolling, setRolling] = useState(false);
  const pushToast = useToastStore((s) => s.push);
  const navigate = useNavigate(); // ST-64: intra-app nav without a full reload

  // ST-75: last played on THIS device (localStorage, written by Player/SongView).
  const [last] = useState<LastPlay | null>(() => readLastPlay());

  const load = useCallback(async () => {
    try {
      const [{ count }, recent, collections, playlists] = await Promise.all([
        apiSongCount(),
        apiSongsRecent(6),
        apiCollections(),
        apiPlaylists(),
      ]);
      setData({ songsExist: count > 0, songCount: count, recent, collections, playlists });
      setError(null);
    } catch (e) {
      // MT-04: offline → the dashboard from the cache (counts, local recent views, playlists).
      if (isNetworkError(e)) {
        const [count, recent, collections, playlists] = await Promise.all([
          offlineSongCount(),
          offlineRecent(6),
          offlineCollections(),
          offlinePlaylists(),
        ]);
        setData({ songsExist: count > 0, songCount: count, recent, collections, playlists });
        setError(null);
        return;
      }
      setError(e instanceof Error ? e.message : 'Could not load the dashboard.');
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  async function rollDice() {
    setRolling(true);
    try {
      const song = await apiSongRandom();
      navigate(`/songs/${song.id}`);
    } catch (e) {
      // MT-04: offline → a random song from the cache.
      if (isNetworkError(e)) {
        const cached = await offlineRandomSong();
        if (cached) {
          navigate(`/songs/${cached.id}`);
          return;
        }
      }
      pushToast(e instanceof Error ? e.message : 'Could not pick a random song.', 'error');
      setRolling(false);
    }
  }

  if (error) {
    return (
      <div className={styles.page}>
        <div className={ui.errorBanner} role="alert">
          <span>{error}</span>
          <button type="button" className={`${ui.btn} ${ui.small}`} onClick={() => void load()}>
            Retry
          </button>
        </div>
      </div>
    );
  }

  if (!data) {
    return (
      <div className={styles.page} aria-busy="true" aria-label="Loading dashboard">
        <div className={ui.skeleton} style={{ height: 96 }} />
        <div className={styles.columns}>
          <div className={ui.skeleton} style={{ height: 240 }} />
          <div className={ui.skeleton} style={{ height: 240 }} />
          <div className={ui.skeleton} style={{ height: 240 }} />
        </div>
      </div>
    );
  }

  if (!data.songsExist) {
    return (
      <div className={styles.page}>
        <div className={`${ui.card} ${styles.onboarding}`}>
          <h2>Welcome to your chord library</h2>
          <p className={ui.muted}>
            It is empty right now. Paste some ChordPro text to add your first song, or convert a
            chord page with the Convert tab on /import.
          </p>
          <div className={styles.onboardActions}>
            <Link to="/import" className={`${ui.btn} ${ui.primary}`}>
              Import a song
            </Link>
            <Link to="/songs/new" className={ui.btn}>
              Write one from scratch
            </Link>
          </div>
        </div>
      </div>
    );
  }

  // The hero's two targets: straight to the player, and the library page.
  const heroPlayUrl =
    last == null
      ? null
      : last.type === 'setlist'
        ? `/playlists/${last.id}/player`
        : `/songs/${last.id}/player`;
  const heroOpenUrl =
    last == null ? null : last.type === 'setlist' ? `/playlists/${last.id}` : `/songs/${last.id}`;

  return (
    <div className={styles.page}>
      {/* ST-75: Jump back in — what this device played last, one tap to the player. */}
      <section className={`${ui.card} ${styles.hero}`} aria-label="Jump back in">
        {last && heroPlayUrl && heroOpenUrl ? (
          <div className={styles.heroMain}>
            <span className={styles.heroKicker}>Jump back in</span>
            <div className={styles.heroTitle}>
              <Link to={heroOpenUrl}>{last.title}</Link>
              {last.artist && (
                <span className={`${ui.muted} ${styles.heroArtist}`}> — {last.artist}</span>
              )}
              {last.type === 'setlist' && (
                <span className={`${ui.badge} ${ui.badgeAccent}`}>Setlist</span>
              )}
            </div>
            <div className={styles.heroActions}>
              <Link to={heroPlayUrl} className={`${ui.btn} ${ui.primary}`}>
                <PlayIcon size={16} /> Play
              </Link>
              <Link to={heroOpenUrl} className={ui.btn}>
                Open
              </Link>
            </div>
          </div>
        ) : (
          <div className={styles.heroMain}>
            <span className={styles.heroKicker}>Welcome back</span>
            <p className={ui.muted}>
              Nothing has been played on this device yet — open a song, or roll for a surprise.
            </p>
          </div>
        )}
        <div className={styles.heroSide}>
          {/* ST-35 C2: shuffle icon replaces the dice (feature kept). ST-35 D6: standard button scale. */}
          <button
            type="button"
            className={`${ui.btn} ${ui.primary}`}
            onClick={rollDice}
            disabled={rolling}
          >
            {rolling ? (
              'Shuffling…'
            ) : (
              <>
                <ShuffleIcon size={18} /> Random
              </>
            )}
          </button>
        </div>
      </section>

      {/* ST-75: one quiet count line replaces the three column count badges. */}
      <p className={styles.counts}>
        {data.songCount} song{data.songCount === 1 ? '' : 's'} · {data.playlists.length} playlist
        {data.playlists.length === 1 ? '' : 's'} · {data.collections.length} collection
        {data.collections.length === 1 ? '' : 's'}
      </p>

      <div className={styles.columns}>
        <section className={`${ui.card} ${styles.column}`} aria-label="Recently played">
          <header className={styles.colHeader}>
            <h2 className={styles.colTitle}>Recently played</h2>
            <Link to="/songs" className={styles.browse}>
              Browse →
            </Link>
          </header>
          {data.recent.length === 0 ? (
            <p className={ui.muted}>
              Nothing played yet. Open a song from search, and it will show up here.{' '}
              <Link to="/import" className={ui.muted}>
                No songs? Import one →
              </Link>
            </p>
          ) : (
            <ul className={styles.colList}>
              {data.recent.map((song) => (
                <li key={song.id}>
                  {/* ST-75: the whole row is the play action — the dashboard is for playing. */}
                  <Link to={`/songs/${song.id}/player`} className={styles.colItem}>
                    <span className={styles.colItemText}>
                      <span className={styles.colItemTitle}>{song.title}</span>
                      {song.artist && (
                        <span className={`${ui.muted} ${styles.colArtist}`}> — {song.artist}</span>
                      )}
                    </span>
                    <PlayIcon size={14} className={styles.colPlay} />
                  </Link>
                </li>
              ))}
            </ul>
          )}
        </section>

        <section className={`${ui.card} ${styles.column}`} aria-label="Playlists">
          <header className={styles.colHeader}>
            <h2 className={styles.colTitle}>Playlists</h2>
            <Link to="/playlists" className={styles.browse}>
              Browse →
            </Link>
          </header>
          {data.playlists.length === 0 ? (
            <p className={ui.muted}>
              No playlists yet. <Link to="/playlists">Create your first one →</Link>
            </p>
          ) : (
            <ul className={styles.colList}>
              {/* Bug report #99: a playlist whose gig is coming up gets top billing. */}
              {[...data.playlists]
                .sort((a, b) => {
                  const ua = a.gigDate && gigUpcoming(a.gigDate) ? 0 : 1;
                  const ub = b.gigDate && gigUpcoming(b.gigDate) ? 0 : 1;
                  if (ua !== ub) return ua - ub;
                  if (ua === 0 && a.gigDate && b.gigDate) return a.gigDate.localeCompare(b.gigDate);
                  return 0;
                })
                .slice(0, 6)
                .map((p) => (
                  <li key={p.id}>
                    <Link to={`/playlists/${p.id}/player`} className={styles.colItem}>
                      <span className={styles.colItemText}>
                        <span className={styles.colItemTitle}>{p.name}</span>
                        {p.gigDate && (
                          <span className={`${ui.badge} ${ui.badgeAccent}`}>
                            {gigLabel(p.gigDate)}
                          </span>
                        )}
                      </span>
                      <span className={`${ui.muted} ${styles.colItemMeta}`}>
                        {p.songCount} song{p.songCount === 1 ? '' : 's'}
                      </span>
                    </Link>
                  </li>
                ))}
            </ul>
          )}
        </section>

        <section className={`${ui.card} ${styles.column}`} aria-label="Collections">
          <header className={styles.colHeader}>
            <h2 className={styles.colTitle}>Collections</h2>
            <Link to="/collections" className={styles.browse}>
              Browse →
            </Link>
          </header>
          {data.collections.length === 0 ? (
            <p className={ui.muted}>
              No collections yet. <Link to="/collections">Create your first one →</Link>
            </p>
          ) : (
            <ul className={styles.colList}>
              {data.collections.slice(0, 6).map((c) => (
                <li key={c.id}>
                  {/* ST-72 grammar: straight to THIS collection's playlists. */}
                  <Link to={`/playlists?collection=${c.id}`} className={styles.colItem}>
                    <span className={styles.colItemText}>
                      <span className={styles.colItemTitle}>{c.name}</span>
                    </span>
                    <span className={`${ui.muted} ${styles.colItemMeta}`}>
                      {c.playlistCount} playlist{c.playlistCount === 1 ? '' : 's'}
                    </span>
                  </Link>
                </li>
              ))}
            </ul>
          )}
        </section>
      </div>
    </div>
  );
}
