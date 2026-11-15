import { useCallback, useEffect, useMemo, useState } from 'react';
import { Link } from 'react-router-dom';
import { apiTags } from '../api/client';
import type { TagRow } from '../api/types';
import { isNetworkError } from '../offline/fallback';
import { offlineTags } from '../offline/query';
import ui from '../components/ui.module.css';
import styles from './Collections.module.css';

/**
 * Tag browser (ST-106): the free tags of the library, with live counts —
 * the retirement of collections. Tags are created where they're used (the song
 * editor, the setlist page); this page browses them: every tag links to the
 * songs it covers and the setlists that carry it.
 */
export default function Collections() {
  const [tags, setTags] = useState<TagRow[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      setTags(await apiTags('tag'));
      setError(null);
    } catch (e) {
      // MT-04: offline → the tags seen on cached songs/playlists (no counts).
      if (isNetworkError(e)) {
        setTags(await offlineTags());
        setError(null);
        return;
      }
      setError(e instanceof Error ? e.message : 'Could not load tags.');
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);

  const sorted = useMemo(
    () => [...(tags ?? [])].sort((a, b) => a.name.localeCompare(b.name)),
    [tags],
  );

  if (error) {
    return (
      <div className={ui.errorBanner} role="alert">
        <span>{error}</span>
        <button type="button" className={`${ui.btn} ${ui.small}`} onClick={() => void load()}>
          Retry
        </button>
      </div>
    );
  }

  return (
    <div className={styles.page}>
      <p className={ui.muted}>
        Tags group songs and setlists the way collections used to — add one in the{' '}
        <Link to="/songs/new">song editor</Link> or on a setlist page.
      </p>

      {tags === null ? (
        <div className={ui.skeleton} style={{ height: 240 }} aria-busy="true" />
      ) : sorted.length === 0 ? (
        <p className={ui.muted}>
          No tags yet — open a song or setlist and add one (“Sunday”, “Worship”, “Stage”…).
        </p>
      ) : (
        <div className={styles.grid}>
          {sorted.map((t) => (
            <div key={t.name} className={`${ui.card} ${styles.card}`}>
              <div className={styles.cardHead}>
                <span className={styles.name}>#{t.name}</span>
              </div>
              <p className={ui.muted}>
                {t.songCount} song{t.songCount === 1 ? '' : 's'} ·{' '}
                {t.playlistCount} setlist{t.playlistCount === 1 ? '' : 's'}
              </p>
              <p className={ui.muted}>
                <Link to={`/songs?tag=${encodeURIComponent(t.name)}`}>Songs →</Link>{' '}
                <Link to={`/playlists?tag=${encodeURIComponent(t.name)}`}>Setlists →</Link>
              </p>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
