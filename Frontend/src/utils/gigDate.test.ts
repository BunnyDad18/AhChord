import { describe, expect, it } from 'vitest';
import { gigLabel, gigUpcoming } from './gigDate';

/** Local yyyy-mm-dd n days from today (the util phrases by local nearness). */
function day(n: number): string {
  const d = new Date();
  d.setDate(d.getDate() + n);
  const p = (x: number) => String(x).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`;
}

describe('gigLabel (bug report #99 phrasing)', () => {
  it('phrases nearness within two weeks', () => {
    expect(gigLabel(day(0))).toBe('Gig today');
    expect(gigLabel(day(1))).toBe('Gig tomorrow');
    expect(gigLabel(day(3))).toBe('Gig in 3 days');
  });

  it('falls back to a plain date past two weeks', () => {
    const expected = new Date('2027-03-05T00:00:00').toLocaleDateString(undefined, { day: 'numeric', month: 'short' });
    expect(gigLabel('2027-03-05')).toBe(expected);
  });

  it('accepts the full ISO stamp the API returns (was "Invalid Date")', () => {
    expect(gigLabel(`${day(2)}T00:00:00Z`)).toBe('Gig in 2 days');
  });
});

describe('gigUpcoming', () => {
  it('is true today and in the future, false in the past', () => {
    expect(gigUpcoming(day(0))).toBe(true);
    expect(gigUpcoming(day(14))).toBe(true);
    expect(gigUpcoming(day(-1))).toBe(false);
    expect(gigUpcoming(`${day(3)}T00:00:00Z`)).toBe(true);
  });
});
