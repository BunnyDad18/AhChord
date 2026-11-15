/**
 * Bug report #83: the avatar shows the FIRST and LAST initial of the display
 * name — "James Taylor" → "JT", one word → "J", empty → "?".
 */
export function initials(name: string): string {
  const words = name.trim().split(/\s+/).filter(Boolean);
  if (words.length === 0) return '?';
  if (words.length === 1) return words[0][0].toUpperCase();
  return (words[0][0] + words[words.length - 1][0]).toUpperCase();
}
