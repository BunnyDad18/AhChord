import type { ColumnPref } from '../utils/columns';
import styles from './ColumnsControl.module.css';

const OPTIONS: { value: ColumnPref; label: string }[] = [
  { value: 'auto', label: 'Auto' },
  { value: 1, label: '1' },
  { value: 2, label: '2' },
  { value: 3, label: '3' },
];

interface ColumnsControlProps {
  value: ColumnPref;
  onChange: (p: ColumnPref) => void;
  /** 'sm' fits the player's compact top bar. */
  size?: 'md' | 'sm';
}

/** ST-42: chart column layout toggle (Auto / 1 / 2 / 3), song page + player. */
export function ColumnsControl({ value, onChange, size = 'md' }: ColumnsControlProps) {
  return (
    <div className={`${styles.toggle} ${size === 'sm' ? styles.sm : ''}`.trim()} role="group" aria-label="Chart columns">
      {OPTIONS.map((o) => (
        <button
          key={String(o.value)}
          type="button"
          className={value === o.value ? styles.active : ''}
          onClick={() => onChange(o.value)}
        >
          {o.label}
        </button>
      ))}
    </div>
  );
}
