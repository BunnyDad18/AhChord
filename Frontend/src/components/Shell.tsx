import { useEffect, useRef, useState } from 'react';
import { NavLink, Outlet, useLocation } from 'react-router-dom';
import { apiInfo, apiConvertList } from '../api/client';
import { useUiStore } from '../stores/ui';
import { useImportQueueStore } from '../stores/importQueue';
import { useToastStore } from '../stores/toast';
import { subscribeToImportEvents } from '../realtime/importClient';
import { onReconnected } from '../realtime/sessionClient';
import { initNetwork } from '../stores/network';
import { syncLibrary } from '../offline/sync';
import { OfflineBanner } from './OfflineBanner';
import { BugReport } from './BugReport';
import { FullscreenToggle } from './FullscreenToggle';
import { ActivityProgress } from './ActivityProgress';
import { BottomNav } from './BottomNav';
import { LogoMark, logoFaviconDataUri, LOGO_COLORS } from './Logos';
import { MoreSheet } from './MoreSheet';
import { SearchBox } from './SearchBox';
import { SessionPanel } from './SessionPanel';
import { UserMenu } from './UserMenu';
import {
  ChevronIcon,
  FolderIcon,
  HomeIcon,
  ImportIcon,
  MoonIcon,
  MusicNoteIcon,
  PlaylistIcon,
  SunIcon,
  SyncIcon,
} from './Icons';
import styles from './Shell.module.css';

/**
 * App shell (ST-75): a left SIDEBAR on wide screens (≥1024px) with the five
 * primary destinations — the exact set the phone bottom tab bar carries —
 * plus the activity box, theme toggle and account menu at its foot. Below
 * 1024px the sidebar is hidden and the top bar's inline nav takes over;
 * below 640px the top bar slims and the bottom tabs arrive.
 */
export function Shell() {
  // ST-39 F1: the song detail page gets the full window width on wide screens (multi-column chart).
  // The import page too — its form + queue need the room (bug report 2026-09-30 16:20).
  const { pathname } = useLocation();
  const isWidePage = /^\/songs\/[^/]+$/.test(pathname) || pathname === '/import';

  const theme = useUiStore((s) => s.theme);
  const toggleTheme = useUiStore((s) => s.toggleTheme);
  const logo = useUiStore((s) => s.logo);

  // Bug report #87: the sidebar can collapse to icons-only (persisted per device).
  // Hovering a collapsed icon flies out a label box (CSS .sideCollapsed rules).
  const [collapsed, setCollapsed] = useState(
    () => localStorage.getItem('ahchord.sidebarCollapsed') === '1',
  );
  useEffect(() => {
    localStorage.setItem('ahchord.sidebarCollapsed', collapsed ? '1' : '0');
  }, [collapsed]);

  // James: the sidebar foot shows the current app version (the server's — the
  // same number the Settings → About badge carries; the native shell reports its
  // APK version in Settings → About).
  const [appVersion, setAppVersion] = useState('');
  useEffect(() => {
    apiInfo()
      .then((i) => setAppVersion(i.version))
      .catch(() => setAppVersion(''));
  }, []);

  // ST-66: the favicon follows the chosen logo + theme brand colour.
  useEffect(() => {
    let link = document.querySelector<HTMLLinkElement>('link[rel="icon"]');
    if (!link) {
      link = document.createElement('link');
      link.rel = 'icon';
      document.head.appendChild(link);
    }
    link.type = 'image/svg+xml';
    link.href = logoFaviconDataUri(logo, LOGO_COLORS[theme]);
  }, [logo, theme]);

  // MT-04: the offline cache — hydrate connectivity, sync the library on open, and
  // re-sync whenever the SignalR connection (the app's reachability probe) comes back.
  useEffect(() => {
    initNetwork();
    void syncLibrary().catch(() => {
      /* offline at start — the cache stays as it is; the online event retries */
    });
    return onReconnected(() => {
      void syncLibrary().catch(() => {});
    });
  }, []);

  // ST-35 H1: the conversion queue is loaded + followed globally (was Import-page only),
  // so the header activity pill works on every page and toasts fire anywhere.
  const upsertList = useImportQueueStore((s) => s.upsertList);
  const mergeList = useImportQueueStore((s) => s.mergeList);
  const applyProgress = useImportQueueStore((s) => s.applyProgress);
  const applyChunk = useImportQueueStore((s) => s.applyChunk);
  const pushToast = useToastStore((s) => s.push);

  // ST-75: the sidebar Import tab carries the "ready to review" badge (same
  // count the More sheet showed).
  const readyCount = useImportQueueStore(
    (s) => s.items.filter((i) => i.status === 'succeeded' && !i.savedSongId).length,
  );

  useEffect(() => {
    let cancelled = false;
    let unsubscribe: (() => void) | null = null;

    apiConvertList()
      .then((list) => {
        if (!cancelled) upsertList(list);
      })
      .catch(() => {
        /* no queue is non-fatal */
      });

    subscribeToImportEvents({
      onProgress: (p) => {
        // A progress event for a draft we don't know about (started in another tab):
        // fetch the list to fill in its label/details without wiping live state.
        if (applyProgress(p)) {
          apiConvertList()
            .then(mergeList)
            .catch(() => {});
        }
        // Report (2026-11-12): a terminal event carries no label/error/finishedAt — pull
        // the authoritative row so the queue converges live, no page reload.
        if (p.status === 'succeeded' || p.status === 'failed') {
          apiConvertList()
            .then(mergeList)
            .catch(() => {});
        }
        if (p.status === 'succeeded') pushToast('A conversion finished — review it in the queue.');
        else if (p.status === 'failed')
          pushToast('A conversion failed — see the queue for details.', 'error');
      },
      onChunk: applyChunk,
      onReconnected: () => {
        apiConvertList()
          .then(upsertList)
          .catch(() => {});
      },
    })
      .then((unsub) => {
        if (cancelled) unsub();
        else unsubscribe = unsub;
      })
      .catch(() => {
        /* hub unavailable — the queue still works via the list fetch */
      });

    return () => {
      cancelled = true;
      unsubscribe?.();
    };
  }, []);

  const linkClass = ({ isActive }: { isActive: boolean }) =>
    isActive ? styles.navLinkActive : styles.navLink;

  const sideLinkClass = ({ isActive }: { isActive: boolean }) =>
    isActive ? styles.sideLinkActive : styles.sideLink;

  // ST-70e: publish the real top-bar height — the Songs sticky filters offset
  // (var(--topbar-height)) was falling back to a 64px value that never existed.
  // Bug report #106: at ≥1024px the topbar is display:none — a hidden element
  // measures offsetHeight 0 and the ResizeObserver fires on that change, so
  // the published value follows to 0 (no consumer is pinned to a stale value).
  const headerRef = useRef<HTMLElement>(null);
  useEffect(() => {
    const el = headerRef.current;
    if (!el) return;
    const publish = () =>
      document.documentElement.style.setProperty('--topbar-height', `${el.offsetHeight}px`);
    publish();
    const ro = new ResizeObserver(publish);
    ro.observe(el);
    return () => ro.disconnect();
  }, []);

  // ST-70d: route changes dismiss the More sheet.
  const setMobileMenuOpen = useUiStore((s) => s.setMobileMenuOpen);
  useEffect(() => setMobileMenuOpen(false), [pathname, setMobileMenuOpen]);

  return (
    <div className={styles.shell}>
      {/* ST-75: desktop sidebar (≥1024px) — the same five destinations as the
          phone bottom tabs, with activity + theme + account at the foot. */}
      <nav
        className={`${styles.sidebar} ${collapsed ? styles.sideCollapsed : ''}`.trim()}
        aria-label="Primary (sidebar)"
      >
        {/* Bug report #86: the brand is static (the Home link is the way home —
            one home affordance, not two). Bug report #107: the head is brand
            only — the collapse chevron moved to the foot icon row. Bug report
            #108: the sidebar brand mark reads bigger at 30 (was 22). */}
        <div className={styles.sideHead}>
          <span className={styles.brand}>
            <span className={styles.logo} aria-hidden="true">
              <LogoMark id={logo} size={30} />
            </span>
            <span className={styles.name}>AhChord</span>
          </span>
        </div>

        <NavLink to="/" end className={sideLinkClass} title="Home">
          <HomeIcon size={18} />
          <span className={styles.sideLabel}>Home</span>
        </NavLink>
        <NavLink to="/songs" className={sideLinkClass} title="Songs">
          <MusicNoteIcon size={18} />
          <span className={styles.sideLabel}>Songs</span>
        </NavLink>
        <NavLink to="/playlists" className={sideLinkClass} title="Playlists">
          <PlaylistIcon size={18} />
          <span className={styles.sideLabel}>Playlists</span>
        </NavLink>
        <NavLink to="/collections" className={sideLinkClass} title="Tags">
          <FolderIcon size={18} />
          <span className={styles.sideLabel}>Tags</span>
        </NavLink>
        <NavLink to="/import" className={sideLinkClass} title="Import">
          <ImportIcon size={18} />
          <span className={styles.sideLabel}>Import</span>
        </NavLink>
        {/* Bug report #103: the queue is a first-class page; the ready badge rides it. */}
        <NavLink to="/queue" className={sideLinkClass} title="Queue">
          <SyncIcon size={18} />
          <span className={styles.sideLabel}>Queue</span>
          {readyCount > 0 && <span className={styles.queueBadge}>{readyCount}</span>}
        </NavLink>

        {/* ST-86 follow-up: the SESSION panel — Start session (setlist picker)
            and a Join row per live session, in the sidebar (James). Collapsed:
            users icon + live count. */}
        <div className={styles.sideSessions}>
          <SessionPanel collapsed={collapsed} />
        </div>

        {/* Bug report #81/#82: activity above a horizontal row of square icon
            buttons. Bug report #107: the chevron led this row; the 2026-11-09
            pass moved it out of the row onto the sidebar's edge (below) —
            the row is theme · report a bug · account. */}
        <div className={styles.sideFoot}>
          <span className={styles.sideActivity}>
            <ActivityProgress />
          </span>
          <div className={styles.sideRow}>
            <button
              type="button"
              className={`${styles.iconBtn} ${styles.sideIcon}`}
              onClick={toggleTheme}
              aria-label={theme === 'dark' ? 'Light mode' : 'Dark mode'}
              title={theme === 'dark' ? 'Light mode' : 'Dark mode'}
            >
              {theme === 'dark' ? <SunIcon size={18} /> : <MoonIcon size={18} />}
            </button>
            {/* Bug report (2026-11-12): full-screen toggle (the browser bar disappears). */}
            <FullscreenToggle className={styles.sideIcon} />
            <BugReport />
            <UserMenu />
          </div>
          {/* James: the current app version, bottom-left of the sidebar (the
              server's version — the same number Settings → About badges). */}
          {appVersion && <span className={styles.sideVersion}>AhChord v{appVersion}</span>}
        </div>

        {/* Collapse/expand — a thin grip tab centred on the sidebar's right
            edge (moved out of the foot row 2026-11-09: it belongs on the
            boundary the sidebar moves along, not among the foot actions). */}
        <button
          type="button"
          className={styles.edgeToggle}
          onClick={() => setCollapsed((v) => !v)}
          aria-label={collapsed ? 'Expand sidebar' : 'Collapse sidebar'}
          aria-expanded={!collapsed}
          title={collapsed ? 'Expand sidebar' : 'Collapse sidebar'}
        >
          <ChevronIcon dir={collapsed ? 'right' : 'left'} size={14} />
        </button>
      </nav>

      <div className={styles.content}>
        <header className={styles.topbar} ref={headerRef}>
          {/* ST-66: pickable logo mark (Settings → Appearance), hidden at ≥1024px.
              Bug report #86: no longer a link home — the Home nav/tab is the one
              home affordance (the brand duplicated it). */}
          <span className={styles.brand}>
            <span className={styles.logo} aria-hidden="true">
              <LogoMark id={logo} size={22} />
            </span>
            <span className={styles.name}>AhChord</span>
          </span>

          <nav className={styles.nav} aria-label="Primary">
            <NavLink to="/" end className={linkClass}>
              Home
            </NavLink>
            <NavLink to="/songs" className={linkClass}>
              Songs
            </NavLink>
            <NavLink to="/playlists" className={linkClass}>
              Playlists
            </NavLink>
            <NavLink to="/collections" className={linkClass}>
              Tags
            </NavLink>
            <NavLink to="/import" className={linkClass}>
              Import
            </NavLink>
            <NavLink to="/queue" className={linkClass}>
              Queue
            </NavLink>
          </nav>

          <span className={styles.searchWrap}>
            <SearchBox />
          </span>

          <div className={styles.actions}>
            {/* ST-70b: activity pills (conversion queue + metadata scan) — only
                rendered while there is activity. Hidden at ≥1024px, where the
                sidebar foot carries it (ST-75). */}
            <span className={styles.wideHidden}>
              <ActivityProgress />
            </span>

            {/* Bug report (2026-11-12): full-screen toggle — the sidebar foot carries
                it at ≥1024px, the top bar below that. */}
            <span className={styles.wideHidden}>
              <FullscreenToggle />
            </span>

            {/* ST-35 H2 / ST-36 C1: in-app bug report (shared component — also on the player).
                Bug report #81: at ≥1024px the sidebar foot carries it instead. */}
            <span className={styles.wideHidden}>
              <BugReport />
            </span>

            {/* ST-70a: the account menu (avatar → name, Settings, light/dark, Sign out).
                Hidden at ≥1024px, where the sidebar foot carries it (ST-75). */}
            <span className={styles.wideHidden}>
              <UserMenu />
            </span>
          </div>
        </header>

        {/* MT-04: offline / stale-cache indicator (only rendered while offline). */}
        <OfflineBanner />

        <main className={`${styles.main} ${isWidePage ? styles.mainWide : ''}`.trim()}>
          <Outlet />
        </main>

        {/* ST-70d: phone-only (CSS) bottom tabs + the More sheet. */}
        <BottomNav />
        <MoreSheet />
      </div>
    </div>
  );
}
