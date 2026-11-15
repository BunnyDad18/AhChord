/**
 * Bug report (2026-11-12): "a button to turn off the chords for someone who is
 * only singing" — showChords is a pure display transform of the renderer.
 */
import { createRoot } from 'react-dom/client';
import { act } from 'react-dom/test-utils';
import { afterEach, describe, expect, it } from 'vitest';
import { SongRenderer } from './SongRenderer';
import type { RenderableSong } from './SongRenderer';

const song: RenderableSong = {
  sections: [
    {
      label: 'Verse',
      type: 'verse',
      lines: [
        {
          text: '[G] Amazing grace',
          chords: [{ name: 'G', charIndex: 0 }],
          annotations: [],
          bars: [],
        },
        { text: '[C] how sweet', chords: [{ name: 'C', charIndex: 0 }], annotations: [], bars: [] },
      ],
    },
  ],
};

let container: HTMLDivElement | null = null;
let root: ReturnType<typeof createRoot> | null = null;

function render(props: { showChords?: boolean }) {
  (globalThis as Record<string, unknown>).IS_REACT_ACT_ENVIRONMENT = true;
  container = document.createElement('div');
  document.body.appendChild(container);
  root = createRoot(container);
  act(() => root!.render(<SongRenderer parsed={song} {...props} />));
}

afterEach(() => {
  act(() => root?.unmount());
  container?.remove();
  root = null;
  container = null;
});

describe('SongRenderer showChords (report 2026-11-12)', () => {
  it('renders chord names by default', () => {
    render({});
    expect(container!.textContent).toContain('Amazing grace');
    expect(container!.textContent).toContain('G');
    expect(container!.textContent).toContain('C');
  });

  it('drops the chord glyphs when chords are off — the words stay', () => {
    render({ showChords: false });
    expect(container!.textContent).toContain('Amazing grace');
    expect(container!.textContent).toContain('how sweet');
    // No chord token renders (names and bracket echoes alike are gone).
    expect(container!.textContent).not.toContain('G');
    expect(container!.textContent).not.toContain('C');
  });
});
