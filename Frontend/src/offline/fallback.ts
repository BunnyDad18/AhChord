/**
 * MT-04: the pages' offline fallback seam.
 *
 * `offlineAware` runs the normal API call; only a *network* failure (ApiError status 0 —
 * server unreachable, which is what offline looks like from fetch) triggers the cache
 * fallback. Server errors (404, 401, validation) propagate unchanged so the UI stays honest.
 */

import { ApiError } from '../api/types';

export function isNetworkError(e: unknown): boolean {
  return e instanceof ApiError && e.status === 0;
}

export async function offlineAware<T>(
  online: () => Promise<T>,
  offline: () => Promise<T | null>,
): Promise<T> {
  try {
    return await online();
  } catch (e) {
    if (isNetworkError(e)) {
      const cached = await offline();
      if (cached !== null) return cached;
    }
    throw e;
  }
}
