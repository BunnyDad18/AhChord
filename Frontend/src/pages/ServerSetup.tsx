import { useId, useState } from 'react';
import { checkServer, normalizeServerUrl, setServerUrl } from '../api/server';
import styles from './ServerSetup.module.css';

/**
 * MT-02: first-run server-address screen for the standalone shell. Shown when
 * the app runs inside the Capacitor shell (phone / Shield Pro) and no server
 * address has been saved. The address is validated by GET {base}/api/health
 * before it is persisted; on success the gate renders the normal app.
 */
export function ServerSetup({ onConnected }: { onConnected: (url: string) => void }) {
  const inputId = useId();
  const [value, setValue] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function connect() {
    setBusy(true);
    setError(null);
    const res = await checkServer(value);
    setBusy(false);
    if (!res.ok) {
      setError(res.error);
      return;
    }
    const base = normalizeServerUrl(value)!;
    setServerUrl(base);
    onConnected(base);
  }

  return (
    <div className={styles.wrap}>
      <form
        className={styles.card}
        onSubmit={(e) => {
          e.preventDefault();
          void connect();
        }}
      >
        <h1 className={styles.title}>AhChord</h1>
        <p className={styles.lede}>Point the app at your AhChord server.</p>

        <label className={styles.label} htmlFor={inputId}>
          Server address
        </label>
        <input
          id={inputId}
          className={styles.input}
          type="text"
          inputMode="url"
          autoComplete="off"
          autoCapitalize="off"
          spellCheck={false}
          placeholder="http://192.168.1.50:5000"
          value={value}
          onChange={(e) => setValue(e.target.value)}
          disabled={busy}
        />
        <p className={styles.hint}>
          The address of the computer running AhChord, including the port (default 5000).
        </p>

        {error && (
          <p className={styles.error} role="alert">
            {error}
          </p>
        )}

        <button type="submit" className={styles.primaryBtn} disabled={busy || !value.trim()}>
          {busy ? 'Connecting…' : 'Connect'}
        </button>
      </form>
    </div>
  );
}
