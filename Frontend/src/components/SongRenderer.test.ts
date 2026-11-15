import { describe, expect, it } from 'vitest';
import { flowPageCount, flowSplit } from './SongRenderer';
import type { Block } from './SongRenderer';

const line = (): Block => ({ kind: 'line', line: { text: 'x', chords: [], annotations: [] } });
const label = (): Block => ({ kind: 'label', text: 'CHORUS' });

const rendered = (blocks: Block[], heights: number[], col: number[]) =>
  col.reduce((sum, bi, i) => sum + heights[bi] + 6 + (blocks[bi].kind === 'label' && i > 0 ? 24 : 0), -6);

// ST-83: the player's fit-to-height flow splits a chart into pages of at most
// the available height, with a 24px slack so a chart that nearly fits stays one page.
describe('flowPageCount (ST-83 fit-to-height pages)', () => {
  it('keeps charts that fit on one page', () => {
    expect(flowPageCount(400, 620)).toBe(1);
    expect(flowPageCount(620, 620)).toBe(1);
    // within the 24px slack — no page break for a hair of overflow
    expect(flowPageCount(640, 620)).toBe(1);
  });

  it('splits the reported song (Come Holy Spirit at 1280×800) into fitted pages', () => {
    // ~1560px of chart in a ~620px viewport → 3 height-fitted pages
    expect(flowPageCount(1560, 620)).toBe(3);
  });

  it('grows the page count as content grows', () => {
    expect(flowPageCount(1300, 620)).toBe(3); // ceil(1276/620)
    expect(flowPageCount(2600, 620)).toBe(5); // ceil(2576/620)
  });

  it('never returns less than 1, even without measurable height', () => {
    expect(flowPageCount(1000, 0)).toBe(1);
    expect(flowPageCount(0, 0)).toBe(1);
  });
});

// ST-83 fix round 2: the packer. The 1024×768 overflow came from flowSplit DROPPING
// the block that triggered a page break when a trailing label moved to the next page —
// a lost line plus a DOM-count desync that disabled pass 2. Conservation is the test.
describe('flowSplit (ST-83 page packing)', () => {
  it('keeps every block exactly once', () => {
    const blocks = [label(), ...Array.from({ length: 20 }, line), label(), ...Array.from({ length: 20 }, line)];
    const heights = blocks.map((b) => (b.kind === 'label' ? 22 : 46));
    const cols = flowSplit(blocks, heights, 300);
    const seen = cols.flat().sort((a, b) => a - b);
    expect(seen).toEqual(blocks.map((_, i) => i));
  });

  it('packs pages to the budget (rendered height incl. gaps and label margins)', () => {
    const blocks = [label(), ...Array.from({ length: 30 }, line)];
    const heights = blocks.map((b) => (b.kind === 'label' ? 22 : 46));
    const cols = flowSplit(blocks, heights, 300);
    for (const col of cols) expect(rendered(blocks, heights, col)).toBeLessThanOrEqual(300);
    // ...and packs, leaving no ~40% air: every non-last page is at least 60% full
    cols.slice(0, -1).forEach((col) => expect(rendered(blocks, heights, col)).toBeGreaterThan(180));
  });

  it('never leaves a section label alone at the bottom of a page', () => {
    // blocks sized so block 3 (a label) is the one that triggers the break
    const blocks: Block[] = [line(), line(), label(), line(), line()];
    const heights = [200, 200, 22, 100, 100];
    const cols = flowSplit(blocks, heights, 420);
    expect(cols.map((c) => c.length)).toEqual([2, 3]); // label starts the next page with its lines
    const seen = cols.flat();
    expect(new Set(seen).size).toBe(5);
  });
});
