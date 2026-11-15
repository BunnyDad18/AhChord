import type { ChordShape } from '../data/chords';
import styles from './ChordDiagram.module.css';

interface ChordDiagramProps {
  shape: ChordShape;
  /** Pixel width of the SVG (height scales with the fret window). */
  width?: number;
}

export const STRING_X_START = 16;
export const STRING_SPACING = 18;
export const FRET_HEIGHT = 22;
export const TOP_PAD = 14;
const BOTTOM_PAD = 6;

/**
 * ST-59: the fret-window computation, extracted as a pure function.
 * Nut on top for open (and low-fret) shapes, otherwise a moved-up window that covers every
 * dot from its lowest to its highest fret. The window must cover every dot: with the nut on
 * top it spans frets 1..numFrets, so numFrets must reach maxFret (the old Math.max(3, span)
 * put Bm's fret-4 and Gm's fret-5 dots BELOW the drawn grid, over the chord label).
 */
export function chordWindow(frets: number[]): { nutOnTop: boolean; startFret: number; numFrets: number } {
  const pressed = frets.filter((f) => f > 0);
  const maxFret = Math.max(0, ...pressed);
  const minFret = pressed.length > 0 ? Math.min(...pressed) : 1;
  const hasOpen = frets.some((f) => f === 0);
  const span = maxFret - minFret + 1;
  // Nut on top when there are open strings, a fret-1 dot (the nut is its upper boundary), or the
  // shape stays in the first five frets. Otherwise a moved-up window whose top wire is one below
  // the lowest dot — the space above that wire is where the lowest dot sits.
  const nutOnTop = hasOpen || minFret === 1 || maxFret <= 5;
  const numFrets = Math.max(3, nutOnTop ? maxFret : span);
  const startFret = nutOnTop ? 1 : minFret - 1;
  return { nutOnTop, startFret, numFrets };
}

/**
 * SVG guitar chord diagram (ST-20). Shows a 3–5 fret window covering the
 * shape's dots, with finger numbers, × (muted) / o (open) markers and the
 * chord name. Theme-aware via CSS variables.
 */
export function ChordDiagram({ shape, width = 150 }: ChordDiagramProps) {
  const { frets, fingers, barre } = shape;
  const { nutOnTop, startFret, numFrets } = chordWindow(frets);

  const height = TOP_PAD + numFrets * FRET_HEIGHT + BOTTOM_PAD + 16;
  const stringX = (i: number) => STRING_X_START + i * STRING_SPACING;
  const fretY = (n: number) => TOP_PAD + (n - startFret) * FRET_HEIGHT;
  // ST-41 fix: a dot sits in the middle of its fret space, and where that space falls relative to
  // the drawn lines differs by diagram type. With the nut on top, space n's upper boundary is the
  // line above it (center = +half a fret height); in a moved-up window, space n's lower boundary
  // is wire n itself (center = -half a fret height). The old single formula placed every dot one
  // fret too high — fret-1 dots rendered above the nut.
  const dotY = (fret: number) =>
    TOP_PAD + (fret - startFret) * FRET_HEIGHT + (nutOnTop ? FRET_HEIGHT / 2 : -FRET_HEIGHT / 2);

  return (
    <svg
      className={styles.diagram}
      width={width}
      height={height}
      viewBox={`0 0 ${width} ${height}`}
      role="img"
      aria-label={`Guitar chord diagram for ${shape.name}`}
    >
      {/* Nut or first fret line */}
      {nutOnTop ? (
        <line
          x1={stringX(0) - 6}
          y1={TOP_PAD - 3}
          x2={stringX(5) + 6}
          y2={TOP_PAD - 3}
          strokeWidth={4}
          className={styles.nut}
        />
      ) : (
        <line
          x1={stringX(0) - 6}
          y1={fretY(startFret)}
          x2={stringX(5) + 6}
          y2={fretY(startFret)}
          strokeWidth={1.5}
          className={styles.fret}
        />
      )}

      {/* Fret lines + fret number for moved-up windows */}
      {Array.from({ length: numFrets + 1 }, (_, i) => startFret + i)
        .filter((n) => (nutOnTop ? n > 1 : true))
        .map((n) => (
          <g key={n}>
            <line
              x1={stringX(0) - 6}
              y1={fretY(n)}
              x2={stringX(5) + 6}
              y2={fretY(n)}
              strokeWidth={1.5}
              className={styles.fret}
            />
            {!nutOnTop && (
              <text x={4} y={fretY(n) - 4} className={styles.fretNum}>
                {n}
              </text>
            )}
          </g>
        ))}

      {/* Strings */}
      {frets.map((_, i) => (
        <line
          key={i}
          x1={stringX(i)}
          y1={TOP_PAD - 6}
          x2={stringX(i)}
          y2={TOP_PAD + numFrets * FRET_HEIGHT + 4}
          strokeWidth={1.5}
          className={styles.string}
        />
      ))}

      {/* Barre */}
      {barre && (
        <rect
          x={stringX(barre.from) - 6}
          y={dotY(barre.fret) - 7}
          width={(barre.to - barre.from + 1) * STRING_SPACING + 12}
          height={14}
          rx={7}
          className={styles.barre}
        />
      )}

      {/* Markers + dots */}
      {frets.map((f, i) => {
        if (f === -1) {
          return (
            <text
              key={i}
              x={stringX(i)}
              y={nutOnTop ? TOP_PAD - 8 : fretY(startFret) - 6}
              className={styles.marker}
              textAnchor="middle"
            >
              ×
            </text>
          );
        }
        if (f === 0) {
          if (!nutOnTop) {
            return (
              <circle key={i} cx={stringX(i)} cy={TOP_PAD - 10} r={5} className={styles.openCircle} />
            );
          }
          return null; // open string with nut visible: no marker needed
        }
        return (
          <g key={i}>
            <circle cx={stringX(i)} cy={dotY(f)} r={8} className={styles.dot} />
            <text
              x={stringX(i)}
              y={dotY(f) + 3.5}
              className={styles.finger}
              textAnchor="middle"
            >
              {fingers[i] || ''}
            </text>
          </g>
        );
      })}

      {/* Chord name */}
      <text
        x={width / 2}
        y={height - 2}
        className={styles.label}
        textAnchor="middle"
      >
        {shape.name}
      </text>
    </svg>
  );
}
