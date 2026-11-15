import { useEffect, useRef, useState } from 'react';
import { DotsIcon } from './Icons';
import ui from './ui.module.css';
import styles from './RowMenu.module.css';

export interface RowMenuItem {
  key: string;
  label: string;
  icon?: React.ReactNode;
  danger?: boolean;
  disabled?: boolean;
  onSelect?: () => void;
}

interface RowMenuProps {
  /** Accessible name of the trigger ("Song actions"). */
  label: string;
  items: RowMenuItem[];
  /** Menu opens flush with the trigger's left edge (top-left triggers). */
  align?: 'right' | 'left';
  /**
   * Bug report #88: a labeled text trigger ("Manage library") instead of the
   * bare ⋯ icon — for page-level menus where a glyph alone would be cryptic.
   */
  triggerLabel?: string;
}

/**
 * ST-75: the shared "⋯" row menu — one grammar for secondary/destructive row
 * actions across Songs / Playlists / Collections / Playlist detail. The trigger
 * is an icon-only button; the menu lists each item with a visible label, so
 * nothing actionable is a bare glyph. Same open/close pattern as the
 * add-to-playlist popover (outside mousedown + Escape). Clicks stop
 * propagation so opening the menu in a clickable row never navigates.
 */
export function RowMenu({ label, items, align = 'right', triggerLabel }: RowMenuProps) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  const triggerRef = useRef<HTMLButtonElement>(null);
  const menuRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    // ST-75 review: a role="menu" pattern expects the keyboard to enter it —
    // focus the first enabled item on open, return focus to the trigger on Esc.
    menuRef.current?.querySelector<HTMLButtonElement>('button:not(:disabled)')?.focus();
    function onDoc(e: MouseEvent) {
      if (ref.current && !ref.current.contains(e.target as Node)) setOpen(false);
    }
    function onKey(e: KeyboardEvent) {
      if (e.key === 'Escape') {
        setOpen(false);
        e.stopPropagation(); // ST-75 review: Esc must not reach the player's exit handler
        triggerRef.current?.focus();
      }
    }
    document.addEventListener('mousedown', onDoc);
    document.addEventListener('keydown', onKey);
    return () => {
      document.removeEventListener('mousedown', onDoc);
      document.removeEventListener('keydown', onKey);
    };
  }, [open]);

  return (
    <div className={styles.wrap} ref={ref} onClick={(e) => e.stopPropagation()}>
      <button
        ref={triggerRef}
        type="button"
        className={`${ui.btn} ${ui.small} ${triggerLabel ? styles.labelTrigger : ui.iconOnly}`}
        aria-label={label}
        aria-haspopup="menu"
        aria-expanded={open}
        title={label}
        onClick={() => setOpen((v) => !v)}
      >
        {triggerLabel ?? <DotsIcon size={16} />}
      </button>
      {open && (
        <div
          ref={menuRef}
          className={`${styles.menu} ${align === 'left' ? styles.alignLeft : ''}`}
          role="menu"
          aria-label={label}
        >
          {items.map((it) => (
            <button
              key={it.key}
              type="button"
              role="menuitem"
              className={`${styles.item} ${it.danger ? styles.danger : ''}`}
              disabled={it.disabled}
              onClick={() => {
                setOpen(false);
                it.onSelect?.();
              }}
            >
              {it.icon && <span className={styles.ico}>{it.icon}</span>}
              <span>{it.label}</span>
            </button>
          ))}
        </div>
      )}
    </div>
  );
}
