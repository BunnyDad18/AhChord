import { useState } from 'react';
import type { ReactNode } from 'react';

/**
 * ST-80 (DRY): the avatar <img> with graceful fallback, duplicated verbatim
 * in UserMenu and BottomNav. The image is served anonymously with a
 * server-issued cache-bust (ST-70c); when there is no image URL, or the load
 * fails, the caller's fallback (the initials) renders.
 */
export function AvatarImg({
  src,
  fallback,
  className,
}: {
  src: string | null;
  fallback: ReactNode;
  className: string;
}) {
  const [failedSrc, setFailedSrc] = useState<string | null>(null);
  if (!src || src === failedSrc) return <>{fallback}</>;
  return <img className={className} src={src} alt="" onError={() => setFailedSrc(src)} />;
}
