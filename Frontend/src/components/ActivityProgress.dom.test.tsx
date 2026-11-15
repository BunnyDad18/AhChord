/**
 * ST-70g variant B: the activity box splits the numbers —
 * {automated work left} (teal, summed over the registry) · {items to review}
 * (amber, the user's backlog). The top-priority automated source wins the icon
 * and the bar; when the machine is done the box flips to the amber "attention"
 * state and links to the review panel.
 */
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { act } from 'react-dom/test-utils';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

vi.mock('../api/client', () => ({
  apiMetadataScanProgress: vi.fn(),
  apiPendingProposalCount: vi.fn(),
}));

import { ActivityProgress } from './ActivityProgress';
import { apiMetadataScanProgress, apiPendingProposalCount } from '../api/client';
import { useImportQueueStore } from '../stores/importQueue';
import { useSessionActivityStore } from '../stores/sessionActivity';

const scan = vi.mocked(apiMetadataScanProgress);
const proposals = vi.mocked(apiPendingProposalCount);

let container: HTMLDivElement | null = null;

async function mount() {
  (globalThis as Record<string, unknown>).IS_REACT_ACT_ENVIRONMENT = true;
  container = document.createElement('div');
  document.body.appendChild(container);
  const root = createRoot(container);
  await act(async () => {
    root.render(
      <StrictMode>
        <MemoryRouter>
          <ActivityProgress />
        </MemoryRouter>
      </StrictMode>,
    );
  });
  await act(async () => {
    await Promise.resolve();
  });
}

beforeEach(() => {
  useSessionActivityStore.getState().setActive(false);
  useImportQueueStore.setState({ items: [] });
  proposals.mockResolvedValue(0);
  scan.mockResolvedValue({ running: false, status: 'none' });
});

afterEach(() => {
  container?.remove();
  container = null;
  vi.clearAllMocks();
});

const box = () => container?.querySelector('a[title]');

describe('ActivityProgress (variant B)', () => {
  it('renders nothing when idle', async () => {
    await mount();
    expect(box()).toBeNull();
  });

  it('review backlog alone: amber attention state, links to the Songs review panel', async () => {
    proposals.mockResolvedValue(134);
    await mount();
    const a = box();
    expect(a).not.toBeNull();
    expect(a!.getAttribute('aria-label')).toContain('134 metadata proposals to review');
    expect(a!.className).toContain('activityPillAttention');
    expect(a!.getAttribute('href')).toContain('/songs');
  });

  it('automated work sums the teal number; review rides as the amber count', async () => {
    scan.mockResolvedValue({ running: true, status: 'running', index: 260, total: 266 });
    proposals.mockResolvedValue(134);
    useImportQueueStore.setState({
      items: [
        { id: 'a', status: 'running' },
        { id: 'b', status: 'queued' },
        { id: 'c', status: 'succeeded', savedSongId: 'x' }, // draft saved: not work
      ],
    } as never);
    await mount();
    const a = box();
    expect(a).not.toBeNull();
    const label = a!.getAttribute('aria-label')!;
    expect(label).toContain('6 of 266 songs left');
    expect(label).toContain('2 songs to convert');
    expect(label).toContain('134 metadata proposals to review');
    // Conversions outrank the scan → the box links to the queue page; no attention state.
    expect(a!.getAttribute('href')).toContain('/queue');
    expect(a!.className).not.toContain('activityPillAttention');
  });

  it('a live playing session wins the icon and the link', async () => {
    useSessionActivityStore.getState().setActive(true);
    scan.mockResolvedValue({ running: true, status: 'running', index: 1, total: 266 });
    await mount();
    const a = box();
    expect(a!.getAttribute('aria-label')).toContain('Playing in a band session');
    expect(a!.getAttribute('href')).toBe('/');
  });
});
