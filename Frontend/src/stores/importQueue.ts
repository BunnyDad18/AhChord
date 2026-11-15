import { create } from 'zustand';
import type { ConversionChunkEvent, ConversionDraftListItem, ConversionProgressEvent } from '../api/types';

/** A queue row: the API summary plus live progress state (ST-33). */
export interface QueueItem extends ConversionDraftListItem {
  /** Characters generated so far this attempt. */
  chars: number;
  /** Current LLM attempt (2 after a retry) — resets the preview buffer when it rises. */
  attempt: number;
  /** Accumulated streamed output for the live preview (in-memory only). */
  previewText: string;
}

interface ImportQueueState {
  /** Newest first. */
  items: QueueItem[];
  /** Full replace from GET /api/import/convert (initial load, reconnect reconciliation). */
  upsertList: (items: ConversionDraftListItem[]) => void;
  /** Merge a fetched list in WITHOUT wiping live state (chars/previewText) of running drafts. */
  mergeList: (items: ConversionDraftListItem[]) => void;
  /** Add a freshly enqueued draft (from POST /api/import/convert) without refetching. */
  addDraft: (item: ConversionDraftListItem) => void;
  /** Returns true when the event created a new item (caller should fetch details for it). */
  applyProgress: (p: ConversionProgressEvent) => boolean;
  applyChunk: (c: ConversionChunkEvent) => void;
  remove: (id: string) => void;
  /** ST-35 E2: after a retry is enqueued — flip the row back to queued locally. */
  markRetried: (id: string) => void;
  /** ST-37 A4: after /save succeeds — flip the row to imported locally (savedSongId). */
  markSaved: (id: string, songId: string) => void;
  /** ST-64: sign-out — drop the queue. Its rows are user data (draft titles, previews);
   * without this, user B briefly sees user A's queue after a re-login in the same tab. */
  clear: () => void;
}

function toItem(i: ConversionDraftListItem): QueueItem {
  return { ...i, chars: 0, attempt: 1, previewText: '' };
}

/**
 * ST-33: the conversion queue. Fed by the initial list fetch and the per-user
 * SignalR events (conversionProgress / conversionChunk). Preview text is in-memory only —
 * a page reload mid-conversion loses it until new chunks arrive (documented).
 */
export const useImportQueueStore = create<ImportQueueState>()((set) => ({
  items: [],

  upsertList: (items) => set({ items: items.map(toItem) }),

  mergeList: (items) =>
    set((s) => {
      const byId = new Map(s.items.map((i) => [i.id, i]));
      for (const item of items) {
        const existing = byId.get(item.id);
        if (existing) {
          // Refresh the static fields; keep live state (chars/previewText) intact.
          byId.set(item.id, {
            ...existing,
            inputUrl: item.inputUrl ?? existing.inputUrl,
            inputPreview: item.inputPreview ?? existing.inputPreview,
            extractedTitle: item.extractedTitle ?? existing.extractedTitle,
            extractedArtist: item.extractedArtist ?? existing.extractedArtist,
            matchesLibrary: item.matchesLibrary ?? existing.matchesLibrary,
            error: item.error ?? existing.error,
            finishedAt: item.finishedAt ?? existing.finishedAt,
          });
        } else {
          byId.set(item.id, toItem(item));
        }
      }
      return { items: [...byId.values()].sort((a, b) => b.createdAt.localeCompare(a.createdAt)) };
    }),

  addDraft: (item) =>
    set((s) => (s.items.some((i) => i.id === item.id) ? s : { items: [toItem(item), ...s.items] })),

  applyProgress: (p) => {
    let created = false;
    set((s) => {
      const existing = s.items.find((i) => i.id === p.id);
      if (!existing) {
        // Started in another tab before this one loaded its list — the page fetches details.
        created = true;
        return {
          items: [
            {
              ...toItem({ id: p.id, status: p.status, createdAt: new Date().toISOString() }),
              chars: p.chars,
              attempt: p.attempt,
            },
            ...s.items,
          ],
        };
      }
      return {
        items: s.items.map((i) =>
          i.id !== p.id
            ? i
            : {
                ...i,
                status: p.status,
                chars: p.chars,
                attempt: p.attempt,
                // Report (2026-11-12): the terminal event must freeze the row — without
                // finishedAt the elapsed clock kept ticking and the row looked unfinished
                // until a page reload ("you have to reload to see the change").
                finishedAt:
                  p.status === 'succeeded' || p.status === 'failed'
                    ? (i.finishedAt ?? new Date().toISOString())
                    : i.finishedAt,
                // A retry regenerates from scratch — drop the stale partial output.
                previewText: p.attempt > i.attempt ? '' : i.previewText,
              },
        ),
      };
    });
    return created;
  },

  applyChunk: (c) =>
    set((s) => ({
      items: s.items.map((i) =>
        i.id === c.id && i.status === 'running'
          ? { ...i, previewText: i.previewText + c.text, chars: i.chars + c.text.length }
          : i,
      ),
    })),

  remove: (id) => set((s) => ({ items: s.items.filter((i) => i.id !== id) })),

  markRetried: (id) =>
    set((s) => ({
      items: s.items.map((i) =>
        i.id === id
          ? { ...i, status: 'queued', error: null, chars: 0, attempt: 1, previewText: '' }
          : i,
      ),
    })),

  markSaved: (id, songId) =>
    set((s) => ({
      items: s.items.map((i) => (i.id === id ? { ...i, savedSongId: songId } : i)),
    })),

  clear: () => set({ items: [] }),
}));
