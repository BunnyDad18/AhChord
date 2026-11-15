import { NavLink } from 'react-router-dom';
import { avatarUrl } from '../api/client';
import { useAuthStore } from '../stores/auth';
import { useImportQueueStore } from '../stores/importQueue';
import { useUiStore } from '../stores/ui';
import { initials } from '../utils/initials';
import { AvatarImg } from './AvatarImg';
import { FolderIcon, HomeIcon, ImportIcon, MusicNoteIcon, PlaylistIcon } from './Icons';
import styles from './Shell.module.css';

/**
 * ST-70d: phone bottom tab bar (CSS: hidden ≥640px). ST-75: the tabs became the
 * five primary destinations — Home · Songs · Playlists · Tags · Import
 * (ready badge) — identical to the desktop sidebar; the Search tab is gone (the
 * Songs page's own search field is one tap away), and Home/Import left the More
 * sheet for the same reason. The avatar tab opens the account sheet.
 *
 * ST-80: the Queue tab (report #103's own tab) is gone from the phone bar —
 * seven slots at 390px left ~46px per label. Queue is transient machinery:
 * the Import page carries the "Open queue" strip, and the ready badge rides
 * the Import tab (its natural context — drafts that converted, awaiting
 * import). The desktop sidebar keeps the Queue link; /queue is routable.
 *
 * ST-70h: the avatar tab (initial / profile photo, no label) is the one entry
 * point to the More sheet (Settings, theme, Sign out).
 */
export function BottomNav() {
  const menuOpen = useUiStore((s) => s.mobileMenuOpen);
  const setMenuOpen = useUiStore((s) => s.setMobileMenuOpen);
  const user = useAuthStore((s) => s.user);
  const readyCount = useImportQueueStore(
    (s) => s.items.filter((i) => i.status === 'succeeded' && !i.savedSongId).length,
  );

  // Same initials/image rules as the desktop UserMenu avatar (bug report #83).
  const name = user?.displayName ?? user?.username ?? user?.email ?? 'Menu';
  const initial = initials(name);
  const img = avatarUrl(user);

  // Both classes when active: .tab carries the layout (flex:1, column, 56px,
  // no underline) — applying ONLY .tabActive collapsed the active tab to a
  // plain underlined link and broke the bar (James, phone testing).
  const linkClass = ({ isActive }: { isActive: boolean }) =>
    isActive ? `${styles.tab} ${styles.tabActive}` : styles.tab;

  return (
    <nav className={styles.bottomNav} aria-label="Primary (phone)">
      <NavLink to="/" end className={linkClass}>
        <HomeIcon size={20} />
        Home
      </NavLink>
      <NavLink to="/songs" className={linkClass}>
        <MusicNoteIcon size={20} />
        Songs
      </NavLink>
      <NavLink to="/playlists" className={linkClass}>
        <PlaylistIcon size={20} />
        Playlists
      </NavLink>
      <NavLink to="/collections" className={linkClass}>
        <FolderIcon size={20} />
        Tags
      </NavLink>
      <NavLink to="/import" className={linkClass}>
        <ImportIcon size={20} />
        {/* ST-80: the queue's ready badge rides the Import tab (see header note). */}
        <span className={styles.tabLabelWrap}>
          Import
          {readyCount > 0 && <span className={styles.tabBadge}>{readyCount}</span>}
        </span>
      </NavLink>
      <button
        type="button"
        className={
          menuOpen
            ? `${styles.tab} ${styles.avatarTab} ${styles.tabActive}`
            : `${styles.tab} ${styles.avatarTab}`
        }
        aria-label="Account"
        aria-expanded={menuOpen}
        onClick={() => setMenuOpen(true)}
      >
        <span className={styles.tabAvatar} aria-hidden="true">
          <AvatarImg src={img} fallback={initial} className={styles.avatarImg} />
        </span>
      </button>
    </nav>
  );
}
