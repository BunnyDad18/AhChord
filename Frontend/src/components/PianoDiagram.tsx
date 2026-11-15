import styles from './PianoDiagram.module.css';

interface PianoDiagramProps {
  /** Note names to highlight, e.g. ['G', 'B', 'D', 'F'] (ST-21). */
  notes: string[];
  width?: number;
}

interface WhiteKey {
  note: string;
  x: number;
}

interface BlackKey {
  note: string;
  x: number;
}

const WHITE_WIDTH = 30;
const KEY_HEIGHT = 100;
const BLACK_WIDTH = 18;
const BLACK_HEIGHT = 62;
const TOP_PAD = 8;

/** C4 -> E5 keyboard layout (10 white, 7 black keys). */
const WHITE_KEYS: WhiteKey[] = [
  { note: 'C', x: 0 },
  { note: 'D', x: WHITE_WIDTH },
  { note: 'E', x: WHITE_WIDTH * 2 },
  { note: 'F', x: WHITE_WIDTH * 3 },
  { note: 'G', x: WHITE_WIDTH * 4 },
  { note: 'A', x: WHITE_WIDTH * 5 },
  { note: 'B', x: WHITE_WIDTH * 6 },
  { note: 'C', x: WHITE_WIDTH * 7 },
  { note: 'D', x: WHITE_WIDTH * 8 },
  { note: 'E', x: WHITE_WIDTH * 9 },
];

const BLACK_KEYS: BlackKey[] = [
  { note: 'C#', x: WHITE_WIDTH * 1 - BLACK_WIDTH / 2 },
  { note: 'D#', x: WHITE_WIDTH * 2 - BLACK_WIDTH / 2 },
  { note: 'F#', x: WHITE_WIDTH * 4 - BLACK_WIDTH / 2 },
  { note: 'G#', x: WHITE_WIDTH * 5 - BLACK_WIDTH / 2 },
  { note: 'A#', x: WHITE_WIDTH * 6 - BLACK_WIDTH / 2 },
  { note: 'C#', x: WHITE_WIDTH * 8 - BLACK_WIDTH / 2 },
  { note: 'D#', x: WHITE_WIDTH * 9 - BLACK_WIDTH / 2 },
];

const ENHARMONIC: Record<string, string> = {
  Db: 'C#',
  Eb: 'D#',
  Gb: 'F#',
  Ab: 'G#',
  Bb: 'A#',
  Cb: 'B',
  Fb: 'E',
  'E#': 'F',
  'B#': 'C',
};

/**
 * SVG piano keyboard C4–E5 with the chord's note names highlighted (ST-21).
 * Every chord gets a piano tooltip — this is the fallback for chords with no
 * guitar shape. Note names render under the highlighted white keys.
 */
export function PianoDiagram({ notes, width = 300 }: PianoDiagramProps) {
  const totalWidth = WHITE_WIDTH * WHITE_KEYS.length;
  const height = TOP_PAD + KEY_HEIGHT + 20;
  const scale = width / totalWidth;

  const normalize = (n: string) => ENHARMONIC[n] ?? n;
  const highlighted = new Set(notes.map(normalize));

  const blackHighlighted = BLACK_KEYS.filter((k) => highlighted.has(k.note));

  return (
    <svg
      className={styles.diagram}
      width={width}
      height={height * scale}
      viewBox={`0 0 ${totalWidth} ${height}`}
      role="img"
      aria-label={`Piano chord diagram highlighting ${notes.join(', ')}`}
    >
      {/* White keys */}
      {WHITE_KEYS.map((k, i) => {
        const on = highlighted.has(k.note);
        return (
          <g key={i}>
            <rect
              x={k.x + 1}
              y={TOP_PAD}
              width={WHITE_WIDTH - 2}
              height={KEY_HEIGHT}
              rx={2}
              className={on ? styles.whiteOn : styles.whiteOff}
            />
            {on && (
              <text
                x={k.x + WHITE_WIDTH / 2}
                y={TOP_PAD + KEY_HEIGHT - 6}
                className={styles.noteLabel}
                textAnchor="middle"
              >
                {k.note}
              </text>
            )}
          </g>
        );
      })}

      {/* Black keys */}
      {BLACK_KEYS.map((k, i) => {
        const on = highlighted.has(k.note);
        return (
          <rect
            key={i}
            x={k.x}
            y={TOP_PAD}
            width={BLACK_WIDTH}
            height={BLACK_HEIGHT}
            rx={2}
            className={on ? styles.blackOn : styles.blackOff}
          />
        );
      })}

      {/* Note names under highlighted black keys (so C#/D#/etc. are readable) */}
      {blackHighlighted.map((k, i) => (
        <text
          key={i}
          x={k.x + BLACK_WIDTH / 2}
          y={TOP_PAD + BLACK_HEIGHT + 12}
          className={styles.noteLabelSmall}
          textAnchor="middle"
        >
          {k.note}
        </text>
      ))}
    </svg>
  );
}
