/**
 * Bug report #99: the playlist gig date, phrased by nearness —
 * "Gig today" / "Gig tomorrow" / "Gig in 3 days" while it's within two weeks,
 * a plain date ("15 Nov") past that. Accepts "yyyy-MM-dd" or a full ISO stamp.
 */
export function gigLabel(gigDate: string): string {
  const day = gigDate.slice(0, 10);
  const d = new Date(`${day}T00:00:00`);
  const today = new Date();
  today.setHours(0, 0, 0, 0);
  const days = Math.round((d.getTime() - today.getTime()) / 86400000);
  if (days === 0) return 'Gig today';
  if (days === 1) return 'Gig tomorrow';
  if (days > 1 && days <= 14) return `Gig in ${days} days`;
  return d.toLocaleDateString(undefined, { day: 'numeric', month: 'short' });
}

/** True when the gig is today or in the future (used to surface it on the dashboard). */
export function gigUpcoming(gigDate: string): boolean {
  const d = new Date(`${gigDate.slice(0, 10)}T23:59:59`);
  return d.getTime() >= Date.now();
}
