import { useEffect } from 'react';
import { useLocation, useNavigate } from 'react-router-dom';
import { useSessionStore } from '../stores/session';

/**
 * ST-87: the follower's auto-follow — renders nothing; while the user follows a
 * session, every new view from the host navigates this device to the same place:
 * the song page, or the setlist player (where the ST-25 transport keeps the songs
 * in sync from then on). Mounted once inside ProtectedRoute (the player is outside
 * the Shell, so the watcher lives above both).
 */
export function SessionWatcher() {
  const navigate = useNavigate();
  const location = useLocation();
  const role = useSessionStore((s) => s.role);
  const viewing = useSessionStore((s) => s.viewing);
  const seq = viewing?.seq ?? -1;

  useEffect(() => {
    if (role !== 'follower' || !viewing) return;
    const target =
      viewing.kind === 'setlist' && viewing.playlistId
        ? `/playlists/${viewing.playlistId}/player`
        : viewing.songId
          ? `/songs/${viewing.songId}`
          : null;
    if (!target || location.pathname === target) return;
    navigate(target, { replace: true });
    // seq: navigate on each NEW view only (not on every location change).
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [role, seq]);

  return null;
}
