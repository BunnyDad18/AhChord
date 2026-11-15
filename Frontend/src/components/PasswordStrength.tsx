import styles from './PasswordStrength.module.css';

interface PasswordStrengthProps {
  password: string;
}

type Level = 'weak' | 'ok' | 'strong';

interface Evaluation {
  level: Level;
  filled: number;
  hint: string;
}

const METER_CLASS: Record<Level, string> = {
  weak: styles.meterWeak,
  ok: styles.meterOk,
  strong: styles.meterStrong,
};

const HINT_CLASS: Record<Level, string> = {
  weak: styles.hintWeak,
  ok: styles.hintOk,
  strong: styles.hintStrong,
};

/** Scores a password on 4 checks (length, case mix, number, symbol). */
function evaluate(password: string): Evaluation | null {
  if (!password) return null;
  const checks = [
    password.length >= 8,
    /[a-z]/.test(password) && /[A-Z]/.test(password),
    /\d/.test(password),
    /[^A-Za-z0-9]/.test(password),
  ];
  const met = checks.filter(Boolean).length;
  const level: Level = met <= 2 ? 'weak' : met === 3 ? 'ok' : 'strong';
  const filled = level === 'weak' ? 1 : level === 'ok' ? 2 : 3;
  const hint =
    level === 'strong'
      ? 'Strong password'
      : level === 'ok'
        ? 'Almost there — add a symbol or number'
        : 'Use 8+ characters with a mix of cases, numbers, and symbols';
  return { level, filled, hint };
}

/** Visual weak/ok/strong meter + hint, shown under the password field. */
export function PasswordStrength({ password }: PasswordStrengthProps) {
  const evald = evaluate(password);
  if (!evald) return null;
  return (
    <div className={styles.wrap} role="status" aria-live="polite">
      <div className={`${styles.meter} ${METER_CLASS[evald.level]}`} aria-hidden="true">
        {[0, 1, 2].map((i) => (
          <span key={i} className={`${styles.seg} ${i < evald.filled ? styles.segFilled : ''}`} />
        ))}
      </div>
      <span className={`${styles.hint} ${HINT_CLASS[evald.level]}`}>{evald.hint}</span>
    </div>
  );
}
