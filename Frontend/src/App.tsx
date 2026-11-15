import { Suspense, lazy } from 'react';
import { createBrowserRouter, RouterProvider } from 'react-router-dom';
import { ProtectedRoute } from './components/ProtectedRoute';
import { ServerGate } from './components/ServerGate';
import { Shell } from './components/Shell';
import { ToastStack } from './components/ToastStack';
import { NotFound } from './components/NotFound';
import styles from './App.module.css';

/**
 * ST-28: every page is a lazy chunk (code splitting); only the shell,
 * guards, and the 404 stay in the initial bundle.
 */
const Dashboard = lazy(() => import('./pages/Dashboard'));
const Songs = lazy(() => import('./pages/Songs'));
const Login = lazy(() => import('./pages/Login'));
const Register = lazy(() => import('./pages/Register'));
const SongView = lazy(() => import('./pages/SongView'));
const SongEditor = lazy(() => import('./pages/SongEditor'));
const Player = lazy(() => import('./pages/Player'));
const Playlists = lazy(() => import('./pages/Playlists'));
const PlaylistDetail = lazy(() => import('./pages/PlaylistDetail'));
const Collections = lazy(() => import('./pages/Collections'));
const Import = lazy(() => import('./pages/Import'));
const Queue = lazy(() => import('./pages/Queue'));
const Settings = lazy(() => import('./pages/Settings'));

function PageLoader() {
  return (
    <div className={styles.loader} role="status" aria-live="polite">
      <div className={styles.loaderBar} />
      <span className={styles.loaderText}>Loading…</span>
    </div>
  );
}

const Susp = (p: { children: React.ReactNode }) => (
  <Suspense fallback={<PageLoader />}>{p.children}</Suspense>
);

/**
 * Route table (ST-15..ST-26). Public: /login, /register, and the 404 catch-all.
 * Everything else is behind ProtectedRoute. The app shell (top bar) wraps the
 * standard pages; the player is fullscreen and sits outside the shell. A data
 * router is used so the song editor can block unsaved navigation (useBlocker).
 */
const router = createBrowserRouter([
  { path: '/login', element: <Susp><Login /></Susp> },
  { path: '/register', element: <Susp><Register /></Susp> },

  {
    element: <ProtectedRoute />,
    children: [
      {
        element: <Shell />,
        children: [
          { path: '/', element: <Susp><Dashboard /></Susp> },
          { path: '/songs', element: <Susp><Songs /></Susp> },
          { path: '/songs/new', element: <Susp><SongEditor mode="new" /></Susp> },
          { path: '/songs/:id', element: <Susp><SongView /></Susp> },
          { path: '/songs/:id/edit', element: <Susp><SongEditor mode="edit" /></Susp> },
          { path: '/playlists', element: <Susp><Playlists /></Susp> },
          { path: '/playlists/:id', element: <Susp><PlaylistDetail /></Susp> },
          { path: '/collections', element: <Susp><Collections /></Susp> },
          { path: '/import', element: <Susp><Import /></Susp> },
          { path: '/queue', element: <Susp><Queue /></Susp> },
          { path: '/settings', element: <Susp><Settings /></Susp> },
        ],
      },
      // Fullscreen player (ST-19): no app shell.
      { path: '/playlists/:id/player', element: <Susp><Player mode="setlist" /></Susp> },
      { path: '/songs/:id/player', element: <Susp><Player mode="single" /></Susp> },
    ],
  },

  { path: '*', element: <NotFound /> },
]);

export default function App() {
  return (
    // MT-02: in the standalone shell this gates on server setup/reachability;
    // in a browser it renders through untouched.
    <ServerGate>
      <RouterProvider router={router} />
      <ToastStack />
    </ServerGate>
  );
}
