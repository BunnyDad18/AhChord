import type { ConversionChunkEvent, ConversionProgressEvent } from '../api/types';
import { getSharedConnection, onReconnected } from './sessionClient';

export interface ImportRealtimeHandlers {
  onProgress?: (p: ConversionProgressEvent) => void;
  onChunk?: (c: ConversionChunkEvent) => void;
  /** The shared connection came back — events were missed while it was down. */
  onReconnected?: () => void;
}

/**
 * ST-33: subscribes to the per-user conversion progress events on the shared hub
 * connection (the server sends them via Clients.User, so every open tab of the user
 * receives them). Returns an unsubscribe function.
 */
export async function subscribeToImportEvents(
  handlers: ImportRealtimeHandlers = {},
): Promise<() => void> {
  const c = await getSharedConnection();

  const progressHandler = (p: ConversionProgressEvent) => handlers.onProgress?.(p);
  const chunkHandler = (chunk: ConversionChunkEvent) => handlers.onChunk?.(chunk);

  c.on('conversionProgress', progressHandler);
  c.on('conversionChunk', chunkHandler);
  // ST-64: the reconnect refresh goes through sessionClient's fan-out — the raw
  // onreconnected setter is single-callback, and a player-session join used to replace
  // this registration (queue missed events app-wide after every reconnect). The old
  // c.off('reconnected', …) was a no-op (off() removes server-event handlers only).
  const offReconnected = onReconnected(() => handlers.onReconnected?.());

  return () => {
    c.off('conversionProgress', progressHandler);
    c.off('conversionChunk', chunkHandler);
    offReconnected();
  };
}
