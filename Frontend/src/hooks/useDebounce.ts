import { useEffect, useState } from 'react';
import type { DependencyList, EffectCallback } from 'react';

/**
 * ST-80 (DRY): the 300 ms search debounce duplicated across Songs/Playlists.
 * Returns `value` after it has been quiet for `ms`; every change resets the
 * timer, and cleanup cancels the pending tick (the exact effect-body pattern
 * the pages used inline).
 */
export function useDebounce<T>(value: T, ms = 300): T {
  const [settled, setSettled] = useState(value);
  useEffect(() => {
    const t = window.setTimeout(() => setSettled(value), ms);
    return () => window.clearTimeout(t);
  }, [value, ms]);
  return settled;
}

/**
 * ST-80 (DRY): the debounce-then-load shape (Playlists) — run `effect` after
 * `deps` have been quiet for `ms`. Same cancellation as useDebounce.
 */
export function useDebouncedEffect(effect: EffectCallback, deps: DependencyList, ms = 300): void {
  // eslint-disable-next-line react-hooks/exhaustive-deps
  useEffect(() => {
    const t = window.setTimeout(effect, ms);
    return () => window.clearTimeout(t);
  }, [...deps, ms]);
}
