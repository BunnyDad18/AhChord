import { describe, expect, it } from 'vitest';
import { LOGO_IDS, logoSvg, logoSvgMarkup } from '../components/Logos';

// ST-66: every pickable logo renders to a well-formed standalone SVG with no
// unbalanced markup — the same builder feeds the top bar, picker, and favicon.
describe('logo marks (ST-66)', () => {
  it('has all concepts registered', () => {
    expect(LOGO_IDS).toEqual([
      'brackets',
      'chordbox',
      'monogram',
      'pick',
      'nut',
      'capo',
      'pegs',
      'classic',
    ]);
  });

  it.each(LOGO_IDS)('mark %s builds valid standalone SVG for both theme colours', (id) => {
    for (const color of ['#4a90c4', '#00838f']) {
      const svg = logoSvg(id, color);
      expect(svg.startsWith('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 96 96">')).toBe(true);
      expect(svg.endsWith('</svg>')).toBe(true);
      expect(logoSvgMarkup(id, color)).not.toBe('');
      expect(svg).toContain(color);
    }
  });
});
