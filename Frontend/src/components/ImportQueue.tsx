import { useEffect, useMemo, useRef, useState } from 'react';
import { apiConvertClearImported, apiConvertDelete, apiConvertImportAll, apiConvertRetry } from '../api/client';
import { useToastStore } from '../stores/toast';
import type { QueueItem } from '../stores/importQueue';
import { useImportQueueStore } from '../stores/importQueue';
import { parseChordPro, looksLikeChordPro } from '../utils/chordpro';
import { SongRenderer } from './SongRenderer';
import { TrashIcon } from './Icons';
import ui from './ui.module.css';
import styles from './ImportQueue.module.css';

interface ImportQueueProps {
  /** Called when the user clicks "Review" on a succeeded draft (loads it into the paste tab). */
  onReview: (id: string) => void;
}

function formatElapsed(ms: number): string {
  const s = Math.max(0, Math.floor(ms / 1000));
  if (s < 60) return `${s}s`;
  const m = Math.floor(s / 60);
  if (m < 60) return `${m}m ${s % 60}s`;
  // ST-37 A3: long conversions show hours, not "95m".
  return `${Math.floor(m / 60)}h ${m % 60}m`;
}

function labelFor(item: QueueItem): string {
  // ST-35 E1: the converted song's name is the most useful identifier once it exists.
  if (item.extractedTitle) {
    return item.extractedArtist
      ? `${item.extractedTitle} — ${item.extractedArtist}`
      : item.extractedTitle;
  }
  if (item.inputPreview) return item.inputPreview;
  if (item.inputUrl) {
    try {
      const u = new URL(item.inputUrl);
      return `${u.host}${u.pathname.length > 40 ? u.pathname.slice(0, 40) + '…' : u.pathname}`;
    } catch {
      return item.inputUrl;
    }
  }
  return 'Conversion';
}

// ST-38 Q1: the single lifecycle pill — QUEUED → PROCESSING → CONVERTED → IMPORTED (+ FAILED).
// The chip CSS uppercases, so these stay lowercase.
function pillLabel(item: QueueItem): string {
  if (item.status === 'succeeded') return item.savedSongId ? 'imported' : 'converted';
  switch (item.status) {
    case 'queued':
      return 'queued';
    case 'running':
      return 'processing';
    default:
      return 'failed';
  }
}

// ST-39 B2: queue ordering — processing → queued → converted → imported; failed last.
function rank(item: QueueItem): number {
  switch (item.status) {
    case 'running':
      return 0;
    case 'queued':
      return 1;
    case 'succeeded':
      return item.savedSongId ? 3 : 2;
    default:
      return 4;
  }
}

/** Debounce a rapidly changing value (the streaming preview text). */
function useDebounced<T>(value: T, ms: number): T {
  const [v, setV] = useState(value);
  useEffect(() => {
    const t = window.setTimeout(() => setV(value), ms);
    return () => window.clearTimeout(t);
  }, [value, ms]);
  return v;
}

/** Live preview of the streamed output: raw text (auto-scrolling) or rendered ChordPro. */
function PreviewPane({ text }: { text: string }) {
  const [mode, setMode] = useState<'raw' | 'rendered'>('raw');
  const preRef = useRef<HTMLPreElement>(null);

  // Keep the raw view pinned to the newest tokens while they stream in.
  useEffect(() => {
    if (mode === 'raw' && preRef.current) {
      preRef.current.scrollTop = preRef.current.scrollHeight;
    }
  }, [text, mode]);

  const debounced = useDebounced(text, 250);
  const parsed = useMemo(() => {
    if (!debounced.trim()) return null;
    const p = parseChordPro(debounced);
    return looksLikeChordPro(p) ? p : null;
  }, [debounced]);

  return (
    <div className={styles.preview}>
      <div className={styles.previewToggle} role="group" aria-label="Preview mode">
        <button
          type="button"
          className={`${ui.btn} ${ui.small} ${mode === 'raw' ? ui.primary : ''}`}
          onClick={() => setMode('raw')}
        >
          Raw text
        </button>
        <button
          type="button"
          className={`${ui.btn} ${ui.small} ${mode === 'rendered' ? ui.primary : ''}`}
          onClick={() => setMode('rendered')}
        >
          Rendered
        </button>
      </div>
      {mode === 'raw' ? (
        <pre ref={preRef} className={styles.previewRaw}>
          {text || 'Waiting for the first tokens…'}
        </pre>
      ) : parsed ? (
        <SongRenderer parsed={parsed} />
      ) : (
        <p className={ui.muted}>No chord content yet — the model is still writing.</p>
      )}
    </div>
  );
}

function QueueRow({
  item,
  now,
  onReview,
}: {
  item: QueueItem;
  now: number;
  onReview: (id: string) => void;
}) {
  const remove = useImportQueueStore((s) => s.remove);
  const markRetried = useImportQueueStore((s) => s.markRetried);
  const pushToast = useToastStore((s) => s.push);
  const [expanded, setExpanded] = useState(false);
  const [busyRemove, setBusyRemove] = useState(false);
  const [busyRetry, setBusyRetry] = useState(false);

  const active = item.status === 'queued' || item.status === 'running';
  const endMs = item.finishedAt ? new Date(item.finishedAt).getTime() : now;
  const elapsedMs = endMs - new Date(item.createdAt).getTime();

  async function handleRemove() {
    setBusyRemove(true);
    try {
      await apiConvertDelete(item.id);
      remove(item.id);
    } catch (e: unknown) {
      pushToast(e instanceof Error ? e.message : 'Could not remove the draft.', 'error');
    } finally {
      setBusyRemove(false);
    }
  }

  // ST-35 E2: re-enqueue a failed conversion; the row flips back to queued locally.
  async function handleRetry() {
    setBusyRetry(true);
    try {
      await apiConvertRetry(item.id);
      markRetried(item.id);
    } catch (e: unknown) {
      pushToast(e instanceof Error ? e.message : 'Could not retry the conversion.', 'error');
    } finally {
      setBusyRetry(false);
    }
  }

  return (
    <li className={styles.row}>
      {/* ST-37 A2: the song name leads; status pill + elapsed time sit under it. */}
      <div className={styles.label} title={labelFor(item)}>
        {labelFor(item)}
      </div>

      <div className={styles.rowFooter}>
        <span className={styles.metaGroup}>
          {/* ST-38 Q1: one pill for the whole lifecycle — no second "In library" badge. */}
          <span className={`${styles.chip} ${styles[`chip_${item.status}`]}`}>
            {pillLabel(item)}
          </span>
          <span className={styles.meta}>
            {formatElapsed(elapsedMs)}
            {item.status === 'running' && item.chars > 0
              ? ` · ${item.chars.toLocaleString()} chars`
              : ''}
            {item.attempt > 1 && active ? ' · retrying…' : ''}
          </span>
        </span>

        <div className={styles.rowActions}>
          {item.status === 'succeeded' && (
            <>
              {/* ST-38 Q1: the Review button disappears once the song has been imported. */}
              {!item.savedSongId && (
                <button
                  type="button"
                  className={`${ui.btn} ${ui.primary} ${ui.small}`}
                  onClick={() => onReview(item.id)}
                >
                  Review
                </button>
              )}
              {/* ST-35 E5: bin icon instead of "Remove" text. */}
              <button
                type="button"
                className={`${ui.btn} ${ui.small} ${ui.iconOnly}`}
                disabled={busyRemove}
                onClick={() => void handleRemove()}
                aria-label={`Remove draft: ${labelFor(item)}`}
                title="Remove draft"
              >
                <TrashIcon size={18} />
              </button>
            </>
          )}
          {item.status === 'failed' && (
            <>
              <button
                type="button"
                className={`${ui.btn} ${ui.small}`}
                disabled={busyRetry}
                onClick={() => void handleRetry()}
              >
                {busyRetry ? 'Retrying…' : 'Retry'}
              </button>
              <button
                type="button"
                className={`${ui.btn} ${ui.small} ${ui.iconOnly}`}
                disabled={busyRemove}
                onClick={() => void handleRemove()}
                aria-label={`Remove draft: ${labelFor(item)}`}
                title="Remove draft"
              >
                <TrashIcon size={18} />
              </button>
            </>
          )}
          {item.status === 'queued' && (
            <button
              type="button"
              className={`${ui.btn} ${ui.small} ${ui.iconOnly} ${ui.danger}`}
              disabled={busyRemove}
              onClick={() => void handleRemove()}
              aria-label={`Cancel conversion: ${labelFor(item)}`}
              title="Cancel conversion"
            >
              <TrashIcon size={18} />
            </button>
          )}
          {item.status === 'running' && (
            /* ST-39 C2: same size as the Review/Retry buttons on this card. */
            <button
              type="button"
              className={`${ui.btn} ${ui.small}`}
              onClick={() => setExpanded((v) => !v)}
            >
              {expanded ? 'Hide preview' : 'Live preview'}
            </button>
          )}
        </div>
      </div>

      {item.status === 'failed' && item.error && <p className={styles.rowError}>{item.error}</p>}
      {expanded && item.status === 'running' && <PreviewPane text={item.previewText} />}
    </li>
  );
}

/**
 * ST-33: the conversion queue on the Import screen. Shows every draft of the user
 * (newest first) with live status, elapsed time and a streaming preview; "Convert in
 * app" enqueues without blocking, so songs can be pasted back-to-back.
 */
export function ImportQueue({ onReview }: ImportQueueProps) {
  const items = useImportQueueStore((s) => s.items);
  const markSaved = useImportQueueStore((s) => s.markSaved);
  const remove = useImportQueueStore((s) => s.remove);
  const pushToast = useToastStore((s) => s.push);
  const [now, setNow] = useState(() => Date.now());
  const [importBusy, setImportBusy] = useState(false);
  const [clearBusy, setClearBusy] = useState(false);

  const activeCount = items.filter((i) => i.status === 'queued' || i.status === 'running').length;
  // ST-73: converted drafts waiting for a song — the "Import all" target.
  const importableCount = items.filter((i) => i.status === 'succeeded' && !i.savedSongId).length;
  // Report (2026-11-12, /queue): "a clear all imported button" — imported rows are spent
  // (the song is in the library); one click retires them instead of binning one by one.
  const importedCount = items.filter((i) => i.savedSongId).length;

  async function clearImported() {
    if (clearBusy) return;
    setClearBusy(true);
    try {
      const r = await apiConvertClearImported();
      for (const i of items) if (i.savedSongId) remove(i.id);
      pushToast(`${r.removed} imported draft${r.removed === 1 ? '' : 's'} cleared from the queue.`);
    } catch (e: unknown) {
      pushToast(e instanceof Error ? e.message : 'Could not clear the queue.', 'error');
    } finally {
      setClearBusy(false);
    }
  }

  // ST-73: one click turns every converted draft into a song (same rules as the per-draft
  // save). Failed rows stay in the queue for individual review; the store flips each saved
  // row to imported locally, no refetch needed.
  async function importAll() {
    if (importBusy) return;
    setImportBusy(true);
    try {
      const r = await apiConvertImportAll();
      for (const s of r.saved) markSaved(s.id, s.songId);
      const parts: string[] = [];
      if (r.imported > 0) parts.push(`${r.imported} song${r.imported === 1 ? '' : 's'} imported`);
      if (r.duplicates > 0) parts.push(`${r.duplicates} already in the library`);
      if (parts.length === 0) parts.push('nothing new to import');
      // The summary keeps its own tone (success/default) — the failure signal is the second
      // toast, so a partial success doesn't read as an all-red error.
      pushToast(`Import all: ${parts.join(', ')}.`);
      if (r.failed > 0) {
        const names = r.failures.map((f) => f.title ?? 'Untitled').join(', ');
        pushToast(
          `${r.failed} draft${r.failed === 1 ? '' : 's'} could not be imported (${names}) — review them individually.`,
          'error',
        );
      }
    } catch (e: unknown) {
      pushToast(e instanceof Error ? e.message : 'Import failed.', 'error');
    } finally {
      setImportBusy(false);
    }
  }

  // Tick the elapsed-time display once a second while anything is in flight.
  useEffect(() => {
    if (activeCount === 0) return;
    setNow(Date.now());
    const t = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(t);
  }, [activeCount]);

  if (items.length === 0) return null;

  // ST-39 B2: lifecycle order — processing, queued, converted, imported (failed last).
  // Stable sort keeps newest-first within each group.
  const ordered = [...items].sort((a, b) => rank(a) - rank(b));

  return (
    <section className={styles.queue} aria-label="Conversion queue">
      {/* ST-73: header row — title + "Import all (N)" over the converted drafts. */}
      <div className={styles.head}>
        <h2 className={styles.title}>
          Conversion queue
          {activeCount > 0 && (
            <span className={styles.activeBadge} role="status" aria-live="polite">
              {activeCount} in progress
            </span>
          )}
        </h2>
        {importableCount > 0 && (
          <button
            type="button"
            className={`${ui.btn} ${ui.primary} ${ui.small}`}
            disabled={importBusy}
            onClick={() => void importAll()}
          >
            {importBusy ? 'Importing…' : `Import all (${importableCount})`}
          </button>
        )}
        {/* Report (2026-11-12): clear the imported rows in one click. */}
        {importedCount > 0 && (
          <button
            type="button"
            className={`${ui.btn} ${ui.small}`}
            disabled={clearBusy}
            onClick={() => void clearImported()}
          >
            {clearBusy ? 'Clearing…' : `Clear imported (${importedCount})`}
          </button>
        )}
      </div>
      <ul className={styles.list}>
        {ordered.map((item) => (
          <QueueRow key={item.id} item={item} now={now} onReview={onReview} />
        ))}
      </ul>
    </section>
  );
}
