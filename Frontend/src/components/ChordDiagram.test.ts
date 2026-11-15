import { describe, expect, it } from 'vitest';
import { FRET_HEIGHT, TOP_PAD, chordWindow } from './ChordDiagram';
import { allShapes } from '../data/chords';

/**
 * ST-59: every dot must land inside the drawn fret grid. The bug: with the nut on top,
 * numFrets = max(3, span) ignored maxFret, so Bm (dots at fret 4) and Gm (fret 5) rendered
 * their dots below the grid, over the chord label.
 */
describe('chordWindow (ST-59)', () => {
  const dotY = (fret: number, w: ReturnType<typeof chordWindow>) =>
    TOP_PAD + (fret - w.startFret) * FRET_HEIGHT + (w.nutOnTop ? FRET_HEIGHT / 2 : -FRET_HEIGHT / 2);

  const shapes: Record<string, number[]> = {
    // the broken ones (nut-on-top shapes whose dots exceed the old 3-fret window)
    Bm: [2, 2, 4, 4, 3, 2],
    Gm: [3, 5, 5, 3, 3, 3],
    'C/G': [-1, 3, 2, 0, 1, 0],
    'D/F#': [2, 0, 0, 2, 3, 2],
    'F/G': [3, 3, 0, 0, 1, 1],
    // open chords (must keep the 3-fret nut-on-top window)
    C: [-1, 3, 2, 0, 1, 0],
    G: [3, 2, 0, 0, 0, 3],
    Em: [0, 2, 2, 0, 0, 0],
    // moved-up windows (verified correct before; must stay correct)
    B: [-1, 2, 4, 4, 4, 2],
    Bb: [-1, -1, 3, 3, 3, 1],
    Csm: [-1, 4, 6, 6, 6, 4],
  };

  for (const [name, frets] of Object.entries(shapes)) {
    it(`${name}: every dot sits inside the drawn grid`, () => {
      const w = chordWindow(frets);
      const gridTop = TOP_PAD; // top wire (nut line / fret 1)
      const gridBottom = TOP_PAD + w.numFrets * FRET_HEIGHT; // last wire
      for (const f of frets) {
        if (f <= 0) continue; // open/muted strings carry no dot
        const y = dotY(f, w);
        expect(y).toBeGreaterThanOrEqual(gridTop);
        expect(y).toBeLessThanOrEqual(gridBottom);
      }
    });
  }

  it('nut-on-top window covers all dots: Bm reaches fret 4, Gm fret 5', () => {
    expect(chordWindow([2, 2, 4, 4, 3, 2])).toEqual({ nutOnTop: true, startFret: 1, numFrets: 4 });
    expect(chordWindow([3, 5, 5, 3, 3, 3])).toEqual({ nutOnTop: true, startFret: 1, numFrets: 5 });
  });

  it('open chords keep the compact 3-fret window', () => {
    expect(chordWindow([-1, 3, 2, 0, 1, 0])).toEqual({ nutOnTop: true, startFret: 1, numFrets: 3 });
    expect(chordWindow([3, 2, 0, 0, 0, 3])).toEqual({ nutOnTop: true, startFret: 1, numFrets: 3 });
  });

  it('moved-up windows are unchanged (Csm starts at fret 3, spans 3)', () => {
    expect(chordWindow([-1, 4, 6, 6, 6, 4])).toEqual({ nutOnTop: false, startFret: 3, numFrets: 3 });
  });

  it('all shapes in the shipped chord library render inside their grid', () => {
    // The repo's own data — the finding named Bm/Gm/C/G/D/F#/F/G from here.
    for (const shape of allShapes()) {
      const w = chordWindow(shape.frets);
      const gridTop = TOP_PAD;
      const gridBottom = TOP_PAD + w.numFrets * FRET_HEIGHT;
      for (const f of shape.frets) {
        if (f <= 0) continue;
        const y = dotY(f, w);
        expect(y, `${shape.name} fret ${f}`).toBeGreaterThanOrEqual(gridTop);
        expect(y, `${shape.name} fret ${f}`).toBeLessThanOrEqual(gridBottom);
      }
    }
  });
});
