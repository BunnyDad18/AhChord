/**
 * Report (2026-11-12, /queue): "when an item in the conversion queue finishes you have to
 * reload the page to see the change" — the progress event flipped the status but not the
 * row's finish: without finishedAt the elapsed clock kept ticking, and the label/error fields
 * only arrived with a reload. applyProgress must freeze the terminal row.
 */
import { describe, expect, it } from 'vitest';
import { useImportQueueStore } from './importQueue';

function seed(id: string) {
  useImportQueueStore
    .getState()
    .upsertList([
      { id, status: 'running', createdAt: new Date(Date.now() - 60_000).toISOString() },
    ]);
}

describe('importQueue store — live terminal states (report 2026-11-12)', () => {
  it('freezes a succeeded row: status flips and finishedAt lands, no reload needed', () => {
    seed('d1');
    useImportQueueStore.getState().applyProgress({
      id: 'd1',
      status: 'succeeded',
      chars: 1200,
      elapsedSeconds: 42,
      attempt: 1,
    });

    const item = useImportQueueStore.getState().items.find((i) => i.id === 'd1')!;
    expect(item.status).toBe('succeeded');
    expect(item.finishedAt).toBeTruthy();
  });

  it('freezes a failed row the same way', () => {
    seed('d2');
    useImportQueueStore
      .getState()
      .applyProgress({ id: 'd2', status: 'failed', chars: 0, elapsedSeconds: 7, attempt: 2 });

    const item = useImportQueueStore.getState().items.find((i) => i.id === 'd2')!;
    expect(item.status).toBe('failed');
    expect(item.finishedAt).toBeTruthy();
  });

  it('leaves finishedAt alone on a running (retry) event', () => {
    seed('d3');
    useImportQueueStore
      .getState()
      .applyProgress({ id: 'd3', status: 'running', chars: 0, elapsedSeconds: 0, attempt: 2 });

    const item = useImportQueueStore.getState().items.find((i) => i.id === 'd3')!;
    expect(item.status).toBe('running');
    expect(item.finishedAt ?? null).toBe(null);
  });
});
