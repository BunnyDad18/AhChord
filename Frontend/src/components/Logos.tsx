/**
 * ST-66: logo marks (bug report 2026-10-19 — "a couple of design options for logos").
 *
 * All concepts rendered as flat currentColor SVG on a 96x96 viewBox; pickable in
 * Settings → Appearance, persisted per browser (`ahchord-ui` store). The same builder
 * feeds the top bar, the Settings picker, and the favicon (data URI), so there is one
 * source of truth per mark.
 */

export type LogoId =
  | 'brackets'
  | 'chordbox'
  | 'monogram'
  | 'pick'
  | 'nut'
  | 'capo'
  | 'pegs'
  | 'classic';

export const LOGO_LABELS: Record<LogoId, string> = {
  brackets: 'ChordPro brackets',
  chordbox: 'Chord box',
  monogram: 'Monogram A',
  pick: 'Guitar pick',
  nut: 'Nut & frets',
  capo: 'Capo bar',
  pegs: 'Headstock',
  classic: 'Classic ♪',
};

export const LOGO_IDS = Object.keys(LOGO_LABELS) as LogoId[];

export const DEFAULT_LOGO: LogoId = 'brackets';

/** Brand secondary per theme (matches --secondary in tokens.css). */
export const LOGO_COLORS: Record<'dark' | 'light', string> = {
  dark: '#4a90c4',
  light: '#00838f',
};

/** Inner SVG (96 viewBox) for a mark, with `color` baked in. One source of truth. */
export function logoSvgMarkup(id: LogoId, color: string): string {
  switch (id) {
    case 'brackets':
      // [C] — the ChordPro chord token; dots strum diagonally inside the brackets.
      return (
        `<g stroke="${color}" stroke-width="8" stroke-linecap="round" fill="none">` +
        `<path d="M30 12 H12 V84 H30"/><path d="M66 12 H84 V84 H66"/></g>` +
        `<g fill="${color}"><circle cx="38" cy="62" r="7"/><circle cx="48" cy="48" r="7"/><circle cx="58" cy="34" r="7"/></g>`
      );
    case 'chordbox': {
      // Miniature chord diagram: nut, strings, frets, one fretted row of dots.
      return (
        `<g stroke="${color}" stroke-width="6" fill="none">` +
        `<path d="M20 20 H76 M24 20 V80 M48 20 V80 M72 20 V80 M24 48 H72 M24 80 H72" stroke-linecap="round"/></g>` +
        `<path d="M20 16 H76" stroke="${color}" stroke-width="10" stroke-linecap="round"/>` +
        `<g fill="${color}"><circle cx="24" cy="64" r="6.5"/><circle cx="48" cy="64" r="6.5"/><circle cx="72" cy="64" r="6.5"/></g>`
      );
    }
    case 'monogram':
      // Geometric A; the crossbar is a fret line, the dot the fingered note.
      return (
        `<g stroke="${color}" stroke-width="9" stroke-linecap="round" fill="none">` +
        `<path d="M18 86 L48 12 L78 86"/><path d="M30 60 H66"/></g>` +
        `<circle cx="48" cy="60" r="8" fill="${color}"/>`
      );
    case 'pick':
      // Flat pick with the classic finger hole (evenodd cuts the hole through).
      return (
        `<path fill="${color}" fill-rule="evenodd" d="M48 90 C 30 66 18 44 18 27 C 18 13 31 8 48 8 ` +
        `C 65 8 78 13 78 27 C 78 44 66 66 48 90 Z M48 21 a 9 9 0 1 0 0.1 0 Z"/>`
      );
    case 'nut':
      // The nut (thick rule) with three strings running down and a barre of dots at fret 2.
      return (
        `<path d="M14 18 H82" stroke="${color}" stroke-width="11" stroke-linecap="round"/>` +
        `<g stroke="${color}" stroke-width="6" stroke-linecap="round">` +
        `<path d="M28 30 V86 M48 30 V86 M68 30 V86"/></g>` +
        `<g fill="${color}"><circle cx="28" cy="56" r="6.5"/><circle cx="48" cy="56" r="6.5"/><circle cx="68" cy="56" r="6.5"/></g>`
      );
    case 'capo':
      // Strings under a capo bar, two notes fretted below it.
      return (
        `<g stroke="${color}" stroke-width="6" stroke-linecap="round">` +
        `<path d="M30 12 V86 M48 12 V86 M66 12 V86"/></g>` +
        `<rect x="16" y="28" width="64" height="12" rx="6" fill="${color}"/>` +
        `<g fill="${color}"><circle cx="30" cy="62" r="6.5"/><circle cx="66" cy="70" r="6.5"/></g>`
      );
    case 'pegs':
      // Headstock with three tuning pegs.
      return (
        `<rect x="36" y="10" width="34" height="76" rx="10" stroke="${color}" stroke-width="6" fill="none"/>` +
        `<g stroke="${color}" stroke-width="5" stroke-linecap="round"><path d="M36 28 H24 M36 48 H24 M36 68 H24"/></g>` +
        `<g fill="${color}"><circle cx="20" cy="28" r="5.5"/><circle cx="20" cy="48" r="5.5"/><circle cx="20" cy="68" r="5.5"/></g>` +
        `<path d="M60 34 V74" stroke="${color}" stroke-width="6" stroke-linecap="round"/>`
      );
    case 'classic':
      // The original ♪ glyph from the top bar, drawn as a path-like text mark.
      return (
        `<text x="48" y="72" font-size="64" text-anchor="middle" fill="${color}" ` +
        `font-family="inherit">&#9834;</text>`
      );
  }
}

/** Full standalone SVG (for the favicon data URI). */
export function logoSvg(id: LogoId, color: string): string {
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 96 96">${logoSvgMarkup(id, color)}</svg>`;
}

export function logoFaviconDataUri(id: LogoId, color: string): string {
  return `data:image/svg+xml,${encodeURIComponent(logoSvg(id, color))}`;
}

/** Inline mark for React (top bar, Settings picker). Uses currentColor when color is omitted. */
export function LogoMark({ id, size, color }: { id: LogoId; size: number; color?: string }) {
  const markup = logoSvgMarkup(id, color ?? 'currentColor');
  return (
    <span
      aria-hidden="true"
      style={{ display: 'inline-flex', width: size, height: size, lineHeight: 0 }}
      // Markup is static per id — no user input ever reaches this.
      dangerouslySetInnerHTML={{
        __html: `<svg viewBox="0 0 96 96" width="${size}" height="${size}" style="display:block">${markup}</svg>`,
      }}
    />
  );
}
