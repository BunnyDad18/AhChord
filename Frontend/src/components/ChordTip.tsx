import { lookupChord } from '../data/chords';
import { chordNotes } from '../utils/chordNotes';
import { ChordDiagram } from './ChordDiagram';
import { PianoDiagram } from './PianoDiagram';
import { useUiStore } from '../stores/ui';
import styles from './ChordName.module.css';

/**
 * Tooltip body (ST-20/21 diagrams + ST-28 lazy chunk). Lives in its own
 * module so the chord shape DB, note math, and SVG diagram components only
 * load with the first opened tooltip, keeping the initial bundle slim.
 */
export default function ChordTip({ name }: { name: string }) {
  const shape = lookupChord(name);
  const notes = chordNotes(name);
  const diagramType = useUiStore((s) => s.diagramType);
  const setDiagramType = useUiStore((s) => s.setDiagramType);

  const showGuitar = diagramType === 'guitar' && shape !== null;

  return (
    <>
      <div className={styles.toggleRow} role="group" aria-label="Diagram type">
        <button
          type="button"
          className={styles.toggleBtn}
          aria-pressed={showGuitar}
          disabled={shape === null}
          title={shape === null ? 'No guitar shape for this chord' : undefined}
          onClick={() => setDiagramType('guitar')}
        >
          Guitar
        </button>
        <button
          type="button"
          className={styles.toggleBtn}
          aria-pressed={!showGuitar}
          onClick={() => setDiagramType('piano')}
        >
          Piano
        </button>
      </div>

      <div className={styles.body}>
        {showGuitar && shape ? (
          <ChordDiagram shape={shape} width={150} />
        ) : (
          <PianoDiagram notes={notes} width={270} />
        )}
      </div>

      {/* <div>, not <p>: the tooltip itself may render inside a line container */}
      <div className={styles.notes}>
        <strong>{name}</strong>: {notes.join(', ')}
      </div>
    </>
  );
}
