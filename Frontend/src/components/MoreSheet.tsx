import { useEffect } from 'react';
import { Link } from 'react-router-dom';
import { PHONE_QUERY, useMediaQuery } from '../hooks/media';
import { useAuthStore } from '../stores/auth';
import { useUiStore } from '../stores/ui';
import { GearIcon, MoonIcon, SignOutIcon, SunIcon } from './Icons';
import styles from './Shell.module.css';

/**
 * ST-70d: the phone account sheet — the avatar tab's sheet. ST-75: Home and
 * Import became bottom tabs (sidebar parity), so what remains is the account
 * header, Settings, the light/dark toggle, Sign out.
 * (2026-11-16: the ST-105 "Report a bug" row is gone — bugs go to GitHub
 * issues, so the sheet is back to account · Settings · theme · Sign out.)
 */
export function MoreSheet() {
  const phone = useMediaQuery(PHONE_QUERY);
  const open = useUiStore((s) => s.mobileMenuOpen);
  const setOpen = useUiStore((s) => s.setMobileMenuOpen);
  const theme = useUiStore((s) => s.theme);
  const toggleTheme = useUiStore((s) => s.toggleTheme);
  const user = useAuthStore((s) => s.user);
  const logout = useAuthStore((s) => s.logout);

  useEffect(() => {
    if (!open) return;
    const onKey = (e: KeyboardEvent) => e.key === 'Escape' && setOpen(false);
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [open, setOpen]);

  if (!open || !phone) return null;

  const close = () => setOpen(false);

  const name = user?.displayName ?? user?.username ?? user?.email ?? 'Account';
  const sub = user?.displayName && user.email ? user.email : user?.username;

  return (
    <>
      <div className={styles.sheetScrim} onClick={close} aria-hidden="true" />
      <div className={styles.sheet} role="dialog" aria-modal="true" aria-label="Account">
        <div className={styles.avatarHeader}>
          <span className={styles.avatarName}>{name}</span>
          {sub && <span className={styles.avatarSub}>{sub}</span>}
        </div>
        <Link to="/settings" role="menuitem" className={styles.avatarItem} onClick={close}>
          <GearIcon size={16} />
          Settings
        </Link>
        <button type="button" role="menuitem" className={styles.avatarItem} onClick={toggleTheme}>
          {theme === 'dark' ? <SunIcon size={16} /> : <MoonIcon size={16} />}
          {theme === 'dark' ? 'Light mode' : 'Dark mode'}
        </button>
        <button
          type="button"
          role="menuitem"
          className={`${styles.avatarItem} ${styles.avatarSignout}`}
          onClick={() => {
            close();
            logout();
          }}
        >
          <SignOutIcon size={16} />
          Sign out
        </button>
      </div>
    </>
  );
}
