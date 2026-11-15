import { Navigate, Outlet, useLocation } from 'react-router-dom';
import { useAuthStore } from '../stores/auth';
import { SessionWatcher } from './SessionWatcher';

/**
 * Gate for authenticated routes. Renders the nested route when a token is present;
 * otherwise redirects to /login (preserving the attempted path in `state.from`).
 * The ST-87 SessionWatcher mounts here (above Shell AND the fullscreen player) —
 * it drives follower navigation while a follow session is live.
 */
export function ProtectedRoute() {
  const token = useAuthStore((s) => s.token);
  const location = useLocation();

  if (!token) {
    return <Navigate to="/login" replace state={{ from: location.pathname }} />;
  }

  return (
    <>
      <SessionWatcher />
      <Outlet />
    </>
  );
}
