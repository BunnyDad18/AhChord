import { useEffect, useRef, useState } from 'react';
import { Link } from 'react-router-dom';
import { avatarUrl } from '../api/client';
import { PHONE_QUERY, useMediaQuery } from '../hooks/media';
import { useAuthStore } from '../stores/auth';
import { useUiStore } from '../stores/ui';
import { initials } from '../utils/initials';
import { AvatarImg } from './AvatarImg';
import { GearIcon, MoonIcon, SignOutIcon, SunIcon } from './Icons';
import styles from './Shell.module.css';

/**
 * ST-70a: the account menu. An avatar button (user's initial — a custom image
 * lands with ST-70c) replacing the old inline "name + Settings + Sign out" text,
 * which ate top-bar width. The menu shows the full name, Settings, the light/dark
 * toggle, and Sign out. Same open/close pattern as the songs add-to-playlist
 * popover: outside mousedown + Escape.
 */
export function UserMenu() {
  const user = useAuthStore((s) => s.user);
  const logout = useAuthStore((s) => s.logout);
  const theme = useUiStore((s) => s.theme);
  const toggleTheme = useUiStore((s) => s.toggleTheme);

  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  // ST-70h: phones get no top-bar avatar at all (see below) — the bottom-nav
  // More tab is the avatar. Desktop renders the dropdown.
  const phone = useMediaQuery(PHONE_QUERY);

  const name = user?.displayName ?? user?.username ?? user?.email ?? 'Account';
  const sub = user?.displayName && user.email ? user.email : user?.username;
  // Bug report #83: first + last initial of the display name.
  const initial = initials(name);

  // ST-70c: profile image when set (URL carries the server's cache-bust version);
  // a failed/missing image falls back to the initial (ST-80: shared AvatarImg).
  const img = avatarUrl(user);

  // Close when clicking anywhere outside the button + menu.
  useEffect(() => {
    if (!open) return;
    function onDoc(e: MouseEvent) {
      if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false);
    }
    function onKey(e: KeyboardEvent) {
      if (e.key === 'Escape') setOpen(false);
    }
    document.addEventListener('mousedown', onDoc);
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('mousedown', onDoc);
      document.removeEventListener('keydown', onKey);
    };
  }, [open]);

  // ST-70h: on phones this component renders nothing — the bottom-nav "More"
  // tab IS the avatar and opens the same sheet (James: "the more, and the
  // Initial are doing the same thing… replace more with the Initial"). The
  // freed top-bar width goes to the activity box. Desktop keeps the dropdown.
  if (phone) return null;

  return (
    <div className={styles.avatarWrap} ref={ref}>
      <button
        type="button"
        className={styles.avatarBtn}
        aria-label="Account"
        aria-haspopup="menu"
        aria-expanded={open}
        onClick={() => setOpen((v) => !v)}
      >
        <AvatarImg src={img} fallback={initial} className={styles.avatarImg} />
      </button>

      {!phone && open && (
        <div className={styles.avatarMenu} role="menu" aria-label="Account">
          <div className={styles.avatarHeader}>
            <span className={styles.avatarName}>{name}</span>
            {sub && <span className={styles.avatarSub}>{sub}</span>}
          </div>
          <Link to="/settings" role="menuitem" className={styles.avatarItem} onClick={() => setOpen(false)}>
            <GearIcon size={16} />
            Settings
          </Link>
          {/* Theme toggle keeps the menu open — switching themes is a look-check, the
              menu updates live (label + sun/moon) so you can flip back and forth. */}
          <button
            type="button"
            role="menuitem"
            className={styles.avatarItem}
            onClick={() => toggleTheme()}
          >
            {theme === 'dark' ? <SunIcon size={16} /> : <MoonIcon size={16} />}
            {theme === 'dark' ? 'Light mode' : 'Dark mode'}
          </button>
          <button
            type="button"
            role="menuitem"
            className={`${styles.avatarItem} ${styles.avatarSignout}`}
            onClick={() => {
              setOpen(false);
              logout();
            }}
          >
            <SignOutIcon size={16} />
            Sign out
          </button>
        </div>
      )}
    </div>
  );
}
