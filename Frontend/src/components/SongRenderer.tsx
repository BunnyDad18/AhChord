import { Fragment, memo, useLayoutEffect, useEffect, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import type { ColumnPref } from '../utils/columns';
import styles from './SongRenderer.module.css';

export interface RenderableLine {
  text: string;
  chords: { name: string; charIndex: number }[];
  annotations: { text: string; charIndex: number }[];
  /** ST-54: bar notation (`[|]`, `[||:]`, `[:||]`) — optional so server-parsed projections (no bars) still fit. */
  bars?: { kind: 'bar' | 'repeat-start' | 'repeat-end'; charIndex: number }[];
}

export interface RenderableSection {
  label: string | null;
  type: string;
  lines: RenderableLine[];
}

export interface RenderableSong {
  header?: unknown;
  sections: RenderableSection[];
}

interface SongRendererProps {
  parsed: RenderableSong;
  /** How a chord token renders (default: plain bold token). Pass <ChordName/> (ST-20) for tooltips. */
  renderChord?: (name: string) => ReactNode;
  /** ST-39 F3: 'inline' (chords in the text flow, default) or 'above' — each chord on its own
   * row directly above the syllable it belongs to (from the ChordPro char positions). */
  chordMode?: 'inline' | 'above';
  /** ST-39 F1 / ST-42: split long songs into balanced columns (song page + player). */
  multiColumn?: boolean;
  /** ST-42: column preference — 'auto' (fit one screen) or a fixed count, capped by width. */
  columns?: ColumnPref;
  /** The lyrics font scale (A−/A+) — line heights depend on it, so column measurement re-runs. */
  fontScale?: number;
  /** ST-42: bump when layout *above* the chart changes (e.g. an async versions box appears) —
   * the available height shifts, so any active plan is dropped and re-measured. */
  layoutEpoch?: number;
  /** ST-83: player on PC/laptop/tablet — every column fits the page height (no vertical
   * scroll) and overflow columns flow to the right, page by page. Below the multi-column
   * width budget (phones) the vertical scroll model stays. */
  flowPages?: boolean;
  /** Bug report (2026-11-12): chords OFF for singers — chord tokens render as plain text
   * (no glyph, no diagram popover). Purely a display transform: the stored .cho text is
   * untouched (AHC-2), and the syllable spacing barely moves (the token is 2–4 chars). */
  showChords?: boolean;
}

/** ST-83: a column plan — `flow` pages are height-fitted and scroll right, showing
 * `visible` columns at once; the classic model lays its columns side by side.
 * `capacity` is the per-page height budget (shrinks when the live guard fires). */
interface LayoutPlan {
  cols: number[][];
  flow: boolean;
  visible: number;
  capacity?: number;
}

type BarKind = 'bar' | 'repeat-start' | 'repeat-end';

// ST-54: raw bracket content per bar kind — used to compute the token's end offset.
const BAR_CONTENT: Record<BarKind, string> = {
  bar: '|',
  'repeat-start': '||:',
  'repeat-end': ':||',
};

interface Token {
  kind: 'chord' | 'ann' | BarKind;
  text: string;
  start: number;
  end: number;
}

/** One run of lyric text, optionally preceded by a chord token. */
interface Segment {
  chord: string | null;
  parts: ReactNode[];
}

/**
 * ST-54: inline glyph for ChordPro bar notation. Drawn as SVG (not the Unicode 𝄆/𝄇) so it
 * renders identically on every platform regardless of font support — the plan's fallback,
 * promoted to primary for determinism. Scales with the lyric font via 1em sizing.
 */
function BarGlyph({ kind }: { kind: BarKind }) {
  if (kind === 'bar') {
    return (
      <svg className={styles.bar} viewBox="0 0 4 24" role="img" aria-label="barline">
        <line x1="2" y1="2" x2="2" y2="22" stroke="currentColor" strokeWidth="1.5" />
      </svg>
    );
  }
  const start = kind === 'repeat-start';
  return (
    <svg
      className={styles.repeat}
      viewBox="0 0 13 24"
      role="img"
      aria-label={start ? 'repeat start' : 'repeat end'}
    >
      {start ? (
        <>
          <circle cx="3.5" cy="9" r="1.5" fill="currentColor" />
          <circle cx="3.5" cy="15" r="1.5" fill="currentColor" />
          <line x1="7.5" y1="2" x2="7.5" y2="22" stroke="currentColor" strokeWidth="1.4" />
          <line x1="11" y1="2" x2="11" y2="22" stroke="currentColor" strokeWidth="3" />
        </>
      ) : (
        <>
          <line x1="2" y1="2" x2="2" y2="22" stroke="currentColor" strokeWidth="3" />
          <line x1="5.5" y1="2" x2="5.5" y2="22" stroke="currentColor" strokeWidth="1.4" />
          <circle cx="9.5" cy="9" r="1.5" fill="currentColor" />
          <circle cx="9.5" cy="15" r="1.5" fill="currentColor" />
        </>
      )}
    </svg>
  );
}

function buildSegments(line: RenderableLine): Segment[] {
  const tokens: Token[] = [
    ...line.chords.map((c) => ({
      kind: 'chord' as const,
      text: c.name,
      start: c.charIndex,
      end: c.charIndex + c.name.length + 2,
    })),
    ...line.annotations.map((a) => ({
      kind: 'ann' as const,
      text: a.text,
      start: a.charIndex,
      end: a.charIndex + a.text.length + 2,
    })),
    ...(line.bars ?? []).map((b) => ({
      kind: b.kind,
      text: BAR_CONTENT[b.kind],
      start: b.charIndex,
      end: b.charIndex + BAR_CONTENT[b.kind].length + 2,
    })),
  ].sort((a, b) => a.start - b.start);

  const out: Segment[] = [];
  let current: Segment | null = null;
  let pos = 0;

  // Emit any pending text-only segment (e.g. lyrics before the first chord on a line)
  // so it is not lost when a new chord segment starts or the line ends.
  const flush = () => {
    if (current && out[out.length - 1] !== current) out.push(current);
    current = null;
  };

  const pushText = (s: string) => {
    if (!s) return;
    (current ??= { chord: null, parts: [] }).parts.push(s);
  };

  tokens.forEach((t, i) => {
    if (t.start > pos) pushText(line.text.slice(pos, t.start));
    if (t.kind === 'chord') {
      // A new segment starts at each chord; the following text belongs to it.
      flush();
      current = { chord: t.text, parts: [] };
      out.push(current);
    } else if (t.kind === 'ann') {
      (current ??= { chord: null, parts: [] }).parts.push(
        <span key={`a${i}`} className={styles.ann} aria-label={`annotation: ${t.text}`}>
          {t.text}
        </span>,
      );
    } else {
      // ST-54: bar notation — inline glyph in the text flow, like annotations.
      (current ??= { chord: null, parts: [] }).parts.push(<BarGlyph key={`b${i}`} kind={t.kind} />);
    }
    pos = Math.max(pos, t.end);
  });
  pushText(line.text.slice(pos));
  flush();

  return out;
}

const SECTION_NAMES: Record<string, string> = {
  verse: 'Verse',
  chorus: 'Chorus',
  bridge: 'Bridge',
  other: '',
};

/** A column is a list of these — labels and lines are the atomic units (never split mid-line). */
export type Block = { kind: 'label'; text: string } | { kind: 'line'; line: RenderableLine };

function buildBlocks(sections: RenderableSection[]): Block[] {
  const blocks: Block[] = [];
  for (const s of sections) {
    if (s.lines.length === 0 && !s.label) continue;
    const label = s.label ?? SECTION_NAMES[s.type];
    if (label) blocks.push({ kind: 'label', text: label });
    for (const line of s.lines) blocks.push({ kind: 'line', line });
  }
  return blocks;
}

/**
 * Greedy balance: walk the blocks in order, closing a column once it reaches its share of the
 * total height. A section label never ends up alone at the bottom of a column — it moves to the
 * next one so it stays with its first line.
 */
function splitBlocks(blocks: Block[], heights: number[], n: number): number[][] {
  const total = heights.reduce((a, b) => a + b, 0);
  const target = total / n;
  const cols: number[][] = [];
  let cur: number[] = [];
  let h = 0;

  for (let i = 0; i < blocks.length; i++) {
    if (cols.length !== n - 1 && h >= target && cur.length > 0) {
      // Close the column. A section label never ends up alone at a column bottom — it moves to
      // the next column so it stays with its first line.
      if (blocks[cur[cur.length - 1]].kind === 'label') {
        const lbl = cur.pop() as number;
        cols.push(cur);
        cur = [lbl];
        h = heights[lbl];
      } else {
        cols.push(cur);
        cur = [];
        h = 0;
      }
    }
    // The last column takes the rest (the guard above never fires for it).
    cur.push(i);
    h += heights[i];
  }
  if (cur.length > 0) cols.push(cur);
  return cols;
}

/**
 * ST-83 (fix round 2): greedy page split for the flow model — fill each page up to
 * `availH`, start a new one when the next block would overflow. The classic model
 * BALANCES columns (equal heights side by side); a paginated chart must PACK: pages
 * full, remainder on the last. Balancing paginated content left every page ~40%
 * empty (the "lot of empty space" report). Raw heights + the 6px gap + the 24px
 * label margin (first-in-page labels carry no margin) so sums are rendered truth.
 * The label-never-orphans rule carries over: a trailing label moves to the next page.
 */
export function flowSplit(blocks: Block[], heights: number[], availH: number): number[][] {
  const cols: number[][] = [];
  let cur: number[] = [];
  let h = 0;
  const add = (i: number, first: boolean) =>
    heights[i] + 6 + (blocks[i].kind === 'label' && !first ? 24 : 0);

  for (let i = 0; i < blocks.length; i++) {
    if (cur.length > 0 && h + add(i, false) > availH) {
      // Close the page; a trailing label never ends a page alone — it starts the next.
      // (The label branch must also place block i — an earlier version moved only the
      // label and DROPPED block i: a lost line plus a desynced DOM count that made
      // pass 2 bail and the pages overflow.)
      if (blocks[cur[cur.length - 1]].kind === 'label') {
        const lbl = cur.pop() as number;
        cols.push(cur);
        cur = [lbl, i];
        h = add(lbl, true) + add(i, false);
      } else {
        cols.push(cur);
        cur = [i];
        h = add(i, true);
      }
    } else {
      cur.push(i);
      h += add(i, cur.length === 1);
    }
  }
  if (cur.length > 0) cols.push(cur);
  return cols;
}

/** ST-42: how many columns a content width can fit before lines get unreadable. */
function maxColumnsFor(width: number): number {
  if (width >= 1100) return 3;
  if (width >= 700) return 2;
  return 1;
}

/**
 * ST-83: page count for the fit-to-height flow model (player, PC/laptop/tablet) —
 * content split into pages of at most `availH`, with a 24px slack so a chart that
 * nearly fits stays a single page.
 */
export function flowPageCount(contentH: number, availH: number): number {
  if (availH <= 0) return 1;
  return Math.max(1, Math.ceil((contentH - 24) / availH));
}

/**
 * ST-83: content width of the chart's scroll context. The flow model paginates by
 * the scroller's width (the player fills the window), not by the single-column
 * reading cap — pass-1 measurement of the classic model still uses clientWidth.
 */
function scrollerWidth(el: HTMLElement): number {
  const anc = el.parentElement;
  if (!anc) return el.clientWidth;
  const cs = getComputedStyle(anc);
  const pad = parseFloat(cs.paddingLeft) + parseFloat(cs.paddingRight) || 0;
  return Math.max(anc.clientWidth - pad, 1);
}

/**
 * ST-42: vertical space left for the chart below `el`, in its scroll context. The song page
 * scrolls the window; the player scrolls an internal container — walk up to find it, and fall
 * back to the viewport bottom otherwise.
 * ST-84 polish: the overlay dock needs no reservation (auto-hide makes overlap
 * fine) — the scroller's padding-bottom is plain breathing room and pages may
 * run the full height; the bar glides over the bottom edge when it appears.
 */
function availableHeight(el: HTMLElement): number {
  const top = el.getBoundingClientRect().top + window.scrollY; // document-relative
  let anc = el.parentElement;
  while (anc) {
    const oy = getComputedStyle(anc).overflowY;
    if (oy === 'auto' || oy === 'scroll') {
      return anc.getBoundingClientRect().bottom + window.scrollY - top - 24;
    }
    anc = anc.parentElement;
  }
  return window.innerHeight + window.scrollY - top - 24;
}

/**
 * Renders parsed ChordPro sections with chord tokens placed above the lyric
 * fragment they precede (ST-18 MVP approach — inline flow, shifted up).
 * ST-39 F3: `chordMode="above"` renders each line as a two-row grid — chords on
 * their own row, aligned column-by-column with the syllables below.
 * Shared by the song view, the player, and the editor/import previews.
 *
 * ST-39 F1 / ST-42: CSS multi-column can't balance an auto-height container, so columns are
 * computed in JS — measure the single column against its scroll context (window or internal
 * scroller), split into 2–3 balanced flex columns when the chart would run off-screen, then
 * re-measure at column width and re-split for accuracy. The count is capped by content width
 * (≥1100px → 3, ≥700px → 2) and can be pinned via `columns` (song page + player).
 */
/**
 * ST-84 polish: CCLI / copyright footer lines — many charts end with bare text
 * like "CCLI Song # 7090414" / "© …" / "CCLI Licence No. …" (no ChordPro
 * marker). At full lyric size they read as part of the song; they are rendered
 * with .lineMeta (small, dim). Conservative prefixes only — real lyric lines
 * essentially never start with these.
 */
const META_LINE_RE =
  /^\s*(ccli\b|cck:|©|℗|for use solely|all rights reserved|note:\s*reproduction|used by permission|reproduced by permission|reprinted by permission|(licence|license)\s*no\b|www\.ccli\.com)/i;

function SongRendererInner({
  parsed,
  renderChord,
  chordMode = 'inline',
  multiColumn = false,
  columns = 'auto',
  fontScale = 1,
  layoutEpoch = 0,
  flowPages = false,
  showChords = true,
}: SongRendererProps) {
  const [plan, setPlan] = useState<LayoutPlan | null>(null);
  // Bumped on resize so the measurement effect re-runs even when plan is already null.
  const [measureTick, setMeasureTick] = useState(0);
  const rootRef = useRef<HTMLDivElement>(null);

  // Content changes invalidate any active plan — drop it so a fresh single-column pass measures.
  // (showChords too: hiding a chord token shrinks the line slightly, so the plan re-measures.)
  useEffect(() => {
    setPlan(null);
  }, [parsed, chordMode, fontScale, columns, layoutEpoch, flowPages, showChords]);

  // ST-42: two-pass column measurement (replaces the old ≥1400px-only gate).
  useLayoutEffect(() => {
    if (!multiColumn) return;
    const el = rootRef.current;
    if (!el) return;

    const blocks = buildBlocks(parsed.sections);
    if (blocks.length === 0) return;
    // ST-83: the player paginates by the scroller's width; the classic model keeps its
    // per-column width budget measured on the element (singleCap in pass 1).
    const flowWidth = flowPages ? scrollerWidth(el) : el.clientWidth;
    const flow = flowPages && maxColumnsFor(flowWidth) >= 2;
    // The width budget caps the count: narrow screens get fewer (or no) columns.
    const maxCols = Math.min(maxColumnsFor(flowWidth), columns === 'auto' ? 3 : columns);
    const availH = availableHeight(el);

    if (plan === null) {
      // Pass 1 — single column on screen: measure it, decide how many columns to split into.
      const nodes = Array.from(el.querySelectorAll<HTMLElement>('[data-block]'));
      if (nodes.length === 0) return;
      const heights = nodes.map((n) => n.getBoundingClientRect().height);
      // Gaps between blocks (~6–12px each) are not in the per-node heights.
      const contentH = heights.reduce((a, b) => a + b, 0) + (nodes.length - 1) * 8;

      if (flow) {
        // ST-83 (fix round 2): PACK pages to the page height (flowSplit), not balance
        // them — balanced pagination left every page ~40% empty.
        //
        // The page count must be decided AT THE PAGE WIDTH: the single-column pass
        // renders at singleCap (880px), where lines wrap far less than on a ~500px
        // page, so its heights understate the chart by ~25% — a plan built from them
        // renders 130% tall pages with inner scrollbars (the 1024×768 case: pass 2
        // bailed on its DOM-vs-block probe, which the section-less single-column pass
        // flow layout trips, so the plan was never re-measured).
        //
        // So: pass 1 packs the REAL single column (the multiColumn single pass IS
        // .singleCap 880px) into pages of width 100/visible via the .multiColRow DOM,
        // then the loop (re-measure at page width → re-pack) converges in the existing
        // two-pass machinery.
        const n0 = Math.max(1, Math.ceil((el.scrollHeight - 24) / availH));
        const cols = n0 <= 1 ? null : flowSplit(blocks, heights, availH);
        setPlan(cols ? { cols, flow: true, visible: maxCols, capacity: availH } : null);
        return;
      }

      let n: number;
      if (columns === 'auto') {
        // Only split when the chart would run off the bottom of its scroll context.
        if (contentH > availH && contentH <= availH * 2 + 60) n = Math.min(2, maxCols);
        else if (contentH > availH * 2 + 60) n = maxCols;
        else n = 1;
      } else {
        // Manual count: honor it (capped by width); very short songs stay single-column.
        n = blocks.length >= columns * 2 ? Math.min(columns, maxCols) : 1;
      }
      setPlan(n <= 1 ? null : { cols: splitBlocks(blocks, heights, n), flow: false, visible: 0 });
      return;
    }

    // Pass 2 — the plan is on screen: re-measure each block at its column width (text wraps
    // more than pass 1 assumed) and re-split with accurate heights. Column widths are equal,
    // so per-block heights are stable and this converges in one iteration.
    const nodes = Array.from(el.querySelectorAll<HTMLElement>('[data-block]'));
    if (nodes.length !== blocks.length) return;
    const byIndex = new Map<number, number>();
    for (const node of nodes)
      byIndex.set(Number(node.dataset.block), node.getBoundingClientRect().height);
    const heights = blocks.map((_, i) => byIndex.get(i) ?? 0);

    if (plan.flow) {
      // ST-83 (fix round 2): re-pack at the real page width — heights measured in
      // pass 2 wrap more than pass 1 assumed. PACKING, not balancing: each page
      // fills to the height budget, the remainder lands on the last page.
      let cap = plan.capacity ?? availH;
      // Live guard for CSS the packing can't see (font metrics, chord lift, rounding):
      // if any on-screen page scrolls inside itself, shrink the budget and re-pack.
      // The plan dependency re-runs this effect, so it converges on no inner scroll.
      // Bug report (2026-11-12, "a scroll bar appears on songs with 3 or more columns"):
      // the shrink floor (availH*0.6) stopped the convergence before the pages fit at
      // 3 columns — the inner scrollbar IS the overflow. The floor is now the tallest
      // block: pages keep shrinking until every page fits (more pages, never a scrollbar).
      const pages = Array.from(el.children) as HTMLElement[];
      if (
        pages.length === plan.cols.length &&
        pages.some((c) => c.scrollHeight > c.clientHeight + 2)
      ) {
        const tallest = heights.reduce((a, b) => Math.max(a, b), 0);
        cap = Math.max(cap - 24, Math.min(availH, tallest + 48));
      }
      const cols = flowSplit(blocks, heights, cap);
      if (JSON.stringify(cols) !== JSON.stringify(plan.cols)) {
        setPlan({ cols, flow: true, visible: flow ? maxCols : plan.visible, capacity: cap });
      }
      return;
    }

    let n = plan.cols.length;
    if (columns === 'auto') {
      // Escalate while the tallest column overflows and the width budget allows more columns.
      for (;;) {
        const p = splitBlocks(blocks, heights, n);
        const colH = p.map((col) => col.reduce((a, bi) => a + (heights[bi] ?? 0), 0));
        if (Math.max(...colH) <= availH || n >= maxCols) break;
        n += 1;
      }
    }
    const cols = splitBlocks(blocks, heights, n);
    if (JSON.stringify(cols) !== JSON.stringify(plan.cols))
      setPlan({ cols, flow: false, visible: 0 });
  }, [
    multiColumn,
    plan,
    parsed,
    chordMode,
    fontScale,
    columns,
    layoutEpoch,
    measureTick,
    flowPages,
  ]);

  // Resize: an active plan is dropped (the measurement effect recomputes); with no plan the tick
  // forces the measurement effect to run — covers e.g. widening into a higher column budget.
  useEffect(() => {
    if (!multiColumn) return;
    let t: number | undefined;
    const onResize = () => {
      window.clearTimeout(t);
      t = window.setTimeout(() => {
        setPlan((p) => (p === null ? p : null));
        setMeasureTick((k) => k + 1);
      }, 150);
    };
    window.addEventListener('resize', onResize);
    return () => {
      window.removeEventListener('resize', onResize);
      window.clearTimeout(t);
    };
  }, [multiColumn]);

  const render = renderChord ?? ((name: string) => <span className={styles.chord}>{name}</span>);

  // Above mode must not inherit the inline token's -0.55em shift.
  const renderAbove = (name: string): ReactNode =>
    renderChord ? renderChord(name) : <span className={styles.chordPlain}>{name}</span>;

  const sections = parsed.sections.filter((s) => s.lines.length > 0 || s.label);
  if (sections.length === 0) {
    return <p className={styles.empty}>No chord content detected yet.</p>;
  }

  function renderLine(line: RenderableLine, key: React.Key) {
    const segs = buildSegments(line);
    // ST-84 polish: CCLI / copyright footer lines (bare text at the end of many
    // charts) read as LOUD lyrics at full size — detect them (conservative line
    // prefixes) and render them small and dim (James: "stand out less").
    const meta = META_LINE_RE.test(line.text);
    const metaCls = meta ? ` ${styles.lineMeta}` : '';

    if (chordMode === 'above') {
      // ST-39 F3: two-row grid — chord row + lyric row, one column per segment.
      // Chords off: the chord cell stays (empty) so the syllable columns keep their
      // exact alignment — the grid is a layout, not the chords' positions.
      return (
        <div key={key} className={`${styles.line} ${styles.lineAbove}${metaCls}`}>
          <div
            className={styles.aboveGrid}
            style={{ gridTemplateColumns: `repeat(${segs.length}, max-content)` }}
          >
            {segs.map((s, i) => (
              <Fragment key={i}>
                <span className={styles.aboveChord}>
                  {s.chord && showChords ? renderAbove(s.chord) : ''}
                </span>
                <span className={styles.aboveText}>{s.parts.length > 0 ? s.parts : '\u00A0'}</span>
              </Fragment>
            ))}
          </div>
        </div>
      );
    }

    // Inline mode (original ST-18 rendering): chord tokens flow in the text.
    return (
      <div key={key} className={`${styles.line}${metaCls}`}>
        {segs.flatMap((s, i) => [
          // Chords off (report 2026-11-12): no glyph, no diagram — the token disappears.
          ...(s.chord && showChords ? [<span key={`c${i}`}>{render(s.chord)}</span>] : []),
          ...s.parts.map((p, pi) =>
            typeof p === 'string' ? <span key={`${i}-${pi}`}>{p}</span> : p,
          ),
        ])}
      </div>
    );
  }

  // Wide-screen multi-column: the measured plan splits blocks into balanced flex columns.
  if (multiColumn && plan) {
    const blocks = buildBlocks(sections);
    const colBlocks = (col: number[]) =>
      col.map((bi) => {
        const b = blocks[bi];
        return b.kind === 'label' ? (
          <h2 key={`l${bi}`} data-block={bi} className={styles.sectionLabel}>
            {b.text}
          </h2>
        ) : (
          <div key={`x${bi}`} data-block={bi}>
            {renderLine(b.line, `m${bi}`)}
          </div>
        );
      });

    // ST-83: fit-to-height pages — columns stand side by side in a horizontal scroller,
    // each exactly page-width (1/`visible`), overflow pages reached by scrolling right.
    if (plan.flow) {
      return (
        <div ref={rootRef} data-flow="true" className={`${styles.song} ${styles.flowRow}`}>
          {plan.cols.map((col, ci) => (
            <div
              key={ci}
              className={`${styles.col} ${styles.flowCol}`}
              style={{ flexBasis: `${100 / plan.visible}%` }}
            >
              {colBlocks(col)}
            </div>
          ))}
        </div>
      );
    }

    return (
      <div ref={rootRef} className={`${styles.song} ${styles.multiColRow}`}>
        {plan.cols.map((col, ci) => (
          <div key={ci} className={styles.col}>
            {colBlocks(col)}
          </div>
        ))}
      </div>
    );
  }

  return (
    <div ref={rootRef} className={`${styles.song} ${multiColumn ? styles.singleCap : ''}`.trim()}>
      {sections.map((section, si) => {
        const label = section.label ?? SECTION_NAMES[section.type];
        // Global block index for measurement — labels and lines in document order.
        let bi = sections
          .slice(0, si)
          .reduce(
            (acc, s) => acc + ((s.label ?? SECTION_NAMES[s.type]) ? 1 : 0) + s.lines.length,
            0,
          );
        return (
          <section key={si} className={styles.section}>
            {/* <h2>: pages render an <h1>, so sections must not skip a level (ST-27). */}
            {label ? (
              <h2 data-block={bi++} className={styles.sectionLabel}>
                {label}
              </h2>
            ) : null}
            <div className={styles.lines}>
              {section.lines.map((line, li) => (
                <div key={li} data-block={bi++}>
                  {renderLine(line, li)}
                </div>
              ))}
            </div>
          </section>
        );
      })}
    </div>
  );
}

// ST-28: memoize so unrelated parent state (transpose, participants, focus) never
// re-renders the whole song body. Callers pass a stable renderChord.
export const SongRenderer = memo(SongRendererInner);
