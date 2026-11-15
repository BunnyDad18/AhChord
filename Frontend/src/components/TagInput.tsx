import { useEffect, useRef, useState } from 'react';
import { apiTags } from '../api/client';
import type { TagRow } from '../api/types';
import ui from './ui.module.css';
import styles from './TagInput.module.css';

interface TagInputProps {
  label: string;
  /** Which tag table the autocomplete queries (ST-106). */
  kind: 'artist' | 'genre' | 'tag';
  value: string[];
  onChange: (next: string[]) => void;
  placeholder?: string;
  /** Artists keep a meaningful order → show ↑/↓ on the chips. */
  reorderable?: boolean;
  /** Secondary line under the label (e.g. the genre case rule). */
  hint?: string;
}

const norm = (s: string) => s.trim().toLowerCase();

/**
 * ST-106 chip input for artists / genres / free tags. Enter or comma commits
 * the typed text, Backspace on an empty input drops the last chip, clicks and
 * suggestions add, ✕ removes, and `reorderable` (artists) moves chips — the
 * chip order is the saved display order (SongTagLink.Position). Autocomplete
 * runs `GET /api/tags?kind=&q=` (debounced 250 ms); offline it simply offers
 * no suggestions — typed names still save.
 */
export function TagInput({
  label,
  kind,
  value,
  onChange,
  placeholder,
  reorderable = false,
  hint,
}: TagInputProps) {
  const [text, setText] = useState('');
  const [suggestions, setSuggestions] = useState<TagRow[]>([]);
  const [open, setOpen] = useState(false);
  const [active, setActive] = useState(-1);
  const rootRef = useRef<HTMLDivElement>(null);
  const run = useRef(0); // fetch race guard (see SearchBox)

  // Debounced autocomplete over the tag table.
  useEffect(() => {
    const q = text.trim();
    const my = ++run.current;
    if (q.length < 1) {
      setSuggestions([]);
      return;
    }
    const t = window.setTimeout(() => {
      apiTags(kind, q)
        .then((rows) => {
          if (my !== run.current) return;
          const taken = new Set(value.map(norm));
          setSuggestions(rows.filter((r) => !taken.has(norm(r.name))).slice(0, 8));
          setOpen(true);
          setActive(-1);
        })
        .catch(() => {
          if (my === run.current) setSuggestions([]);
        });
    }, 250);
    return () => window.clearTimeout(t);
  }, [text, kind, value]);

  // Close on outside click.
  useEffect(() => {
    const onDown = (e: MouseEvent) => {
      if (rootRef.current && !rootRef.current.contains(e.target as Node)) setOpen(false);
    };
    document.addEventListener('mousedown', onDown);
    return () => document.removeEventListener('mousedown', onDown);
  }, []);

  function add(raw: string) {
    const name = raw.trim().replace(/\s+/g, ' ');
    if (!name) return;
    if (!value.some((v) => norm(v) === norm(name))) onChange([...value, name]);
    setText('');
    setSuggestions([]);
    setOpen(false);
  }

  function removeAt(i: number) {
    onChange(value.filter((_, j) => j !== i));
  }

  function move(i: number, dir: -1 | 1) {
    const j = i + dir;
    if (j < 0 || j >= value.length) return;
    const next = [...value];
    [next[i], next[j]] = [next[j], next[i]];
    onChange(next);
  }

  function onKeyDown(e: React.KeyboardEvent<HTMLInputElement>) {
    if (e.key === 'Enter' || e.key === ',') {
      e.preventDefault();
      if (active >= 0 && suggestions[active]) add(suggestions[active].name);
      else add(text);
    } else if (e.key === 'Escape') {
      setOpen(false);
      setActive(-1);
    } else if (e.key === 'ArrowDown' && suggestions.length > 0) {
      e.preventDefault();
      setOpen(true);
      setActive((a) => (a + 1) % suggestions.length);
    } else if (e.key === 'ArrowUp' && suggestions.length > 0) {
      e.preventDefault();
      setActive((a) => (a - 1 + suggestions.length) % suggestions.length);
    } else if (e.key === 'Backspace' && text === '' && value.length > 0) {
      removeAt(value.length - 1);
    }
  }

  return (
    <div className={styles.root} ref={rootRef}>
      <span className={styles.label}>{label}</span>
      {hint && <span className={styles.hint}>{hint}</span>}
      <div className={styles.chips}>
        {value.map((name, i) => (
          <span key={`${name}-${i}`} className={styles.chip}>
            {name}
            {reorderable && (
              <span className={styles.reorder}>
                <button
                  type="button"
                  className={styles.reorderBtn}
                  aria-label={`Move ${name} earlier`}
                  disabled={i === 0}
                  onClick={() => move(i, -1)}
                >
                  ↑
                </button>
                <button
                  type="button"
                  className={styles.reorderBtn}
                  aria-label={`Move ${name} later`}
                  disabled={i === value.length - 1}
                  onClick={() => move(i, 1)}
                >
                  ↓
                </button>
              </span>
            )}
            <button
              type="button"
              className={styles.removeBtn}
              aria-label={`Remove ${name}`}
              onClick={() => removeAt(i)}
            >
              ✕
            </button>
          </span>
        ))}
        <input
          className={styles.input}
          value={text}
          onChange={(e) => setText(e.target.value.replace(/,/g, ' '))}
          onKeyDown={onKeyDown}
          onFocus={() => suggestions.length > 0 && setOpen(true)}
          placeholder={value.length === 0 ? placeholder : ''}
          aria-label={label}
          aria-expanded={open}
        />
      </div>
      {open && suggestions.length > 0 && (
        <ul className={styles.suggestions} role="listbox">
          {suggestions.map((s, i) => (
            <li key={`${s.kind}-${s.name}`}>
              <button
                type="button"
                className={`${styles.suggestion} ${i === active ? styles.suggestionActive : ''}`}
                role="option"
                aria-selected={i === active}
                onMouseDown={(e) => {
                  e.preventDefault(); // keep input focus while the list closes
                  add(s.name);
                }}
              >
                {s.name}
                <span className={ui.muted}>
                  {s.songCount} song{s.songCount === 1 ? '' : 's'}
                  {s.kind === 'tag' && s.playlistCount > 0
                    ? ` · ${s.playlistCount} setlist${s.playlistCount === 1 ? '' : 's'}`
                    : ''}
                </span>
              </button>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
