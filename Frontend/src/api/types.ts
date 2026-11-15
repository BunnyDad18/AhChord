// DTO types mirroring the WebApi (camelCase JSON, per ASP.NET Core defaults).
// These are the contract the frontend relies on; keep them in sync with WebApi/Dtos.

// ---------- Auth ----------

export interface AuthUser {
  /** ST-70c: stable user id — avatar URL builder. */
  id: string;
  email: string;
  /** ST-35 A1: unique login name (legacy accounts have their email here). */
  username: string | null;
  displayName: string | null;
  /** ST-30: first registered account — may edit the band-wide LLM default. */
  isAdmin?: boolean;
  /** ST-70c: cache-bust stamp (server ticks) for the avatar URL; null = no image. */
  avatarVersion?: number | null;
}

export interface AuthResponse {
  token: string;
  expiresIn: number;
  user: AuthUser;
}

/** ST-35 A1: send the identifier in `username` or `email` — the server tries both. */
export interface LoginRequest {
  username?: string;
  email?: string;
  password: string;
}

export interface RegisterRequest {
  /** ST-35 A1: unique login name, 3-32 chars of letters/numbers/.-_ */
  username: string;
  email: string;
  password: string;
  displayName?: string;
}

// ---------- Songs ----------

export interface Song {
  id: string;
  title: string;
  artist: string | null;
  album: string | null;
  year: number | null;
  key: string | null;
  /** ST-35 F2/D4: musical genre (LLM-populated during conversion, editable). */
  genre: string | null;
  /** ST-35 F3: approximate tempo in BPM (proposed by the metadata review job). */
  tempo: number | null;
  capoFret: number;
  createdAt: string;
  updatedAt: string;
  /**
   * ST-106: multi-valued artists/genres/free tags from the tag link tables (the truth;
   * the flat `artist`/`genre` fields above are the legacy rollback cache).
   */
  artists?: string[] | null;
  genres?: string[] | null;
  tags?: string[] | null;
}

/** ST-35 F3: one proposed metadata change for a song (pending until accepted/rejected). */
export interface MetadataProposal {
  id: string;
  /** "title" | "artist" | "genre" | "year" | "key" | "tempo". */
  field: string;
  currentValue: string | null;
  proposedValue: string;
  createdAt: string;
}

/** ST-69: one song inside a library-wide proposal group. */
export interface ProposalGroupSong {
  songId: string;
  title: string;
  artist: string | null;
  currentValue: string | null;
}

/** ST-69: all pending proposals for one field + proposed value (e.g. genre → "Rock"). */
export interface ProposalGroup {
  field: string;
  value: string;
  count: number;
  songs: ProposalGroupSong[];
}

export interface SongDetail extends Song {
  chordProText: string;
  sourceUrl: string | null;
  /**
   * ST-106: key-derivation state. `keyOverride` = the key was hand-set (the chord
   * analyzer won't overwrite it); `keyConfidence` = the analyzer's confidence for
   * the current key, 0..1 (null when never detected).
   */
  keyOverride?: boolean;
  keyConfidence?: number | null;
}

/** Song + the time the current user last viewed it (GET /api/songs/recent). */
export interface RecentSong extends Song {
  viewedAt: string;
}

/** ST-35 D4/D5: distinct filter values for the songs-list filter panel. */
export interface SongFacets {
  artists: string[];
  genres: string[];
  decades: number[];
  /** ST-39 C1: total song groups (title+artist) — the unfiltered list length. */
  total: number;
  /** ST-106: distinct free-tag names for the tag chip row. */
  tags?: string[] | null;
}

/**
 * ST-106: one tag row from GET /api/tags (autocomplete + the tag browser).
 * `songCount` covers every kind; `playlistCount` is meaningful for kind="tag".
 */
export interface TagRow {
  kind: 'artist' | 'genre' | 'tag';
  name: string;
  songCount: number;
  playlistCount: number;
}

/** ST-106: result of POST /api/songs/{id}/detect-key. */
export interface DetectedKey {
  key: string;
  confidence: number;
}

/**
 * ST-68: live progress of the full-library metadata scan.
 * `running` drives the Songs-page strip; a finished/interrupted row is reported
 * (status ≠ running) so the UI can toast once on the running→not-running edge.
 * `etaSeconds` is a coarse rate estimate — null until a rate exists.
 */
export interface MetadataScanProgress {
  running: boolean;
  status: string; // "none" | "running" | "done" | "interrupted"
  current?: string | null;
  index?: number;
  total?: number;
  startedAt?: string;
  elapsedSeconds?: number;
  etaSeconds?: number | null;
  proposalsAdded?: number;
}

/** ST-35 F1: one row per song (title+artist) with its versions collapsed. */
export interface SongGroup {
  title: string;
  artist: string | null;
  versionCount: number;
  /** Most recently created version — what a list-row click plays. */
  latest: Song;
  /** Every version id, oldest first. */
  ids: string[];
  /** ST-35 F3: pending metadata proposals on the latest version (drives the Review button). */
  pendingProposals: number;
}

export interface CreateSongRequest {
  title?: string;
  artist?: string;
  album?: string;
  year?: number;
  key?: string;
  genre?: string;
  /** ST-52: approximate tempo in BPM (30–300). */
  tempo?: number;
  capoFret?: number;
  chordProText?: string;
  sourceUrl?: string;
  /**
   * ST-106: tag lists — a sent list replaces that kind wholesale, absent = unchanged.
   * Artists keep entered spelling and order; genres are title-cased by the server.
   */
  artists?: string[];
  genres?: string[];
  tags?: string[];
}

export type UpdateSongRequest = CreateSongRequest;

// ---------- Parsed ChordPro (read-time projection) ----------

export interface ChordToken {
  name: string;
  charIndex: number;
}

export interface Annotation {
  text: string;
  charIndex: number;
}

/** ST-54 bar notation token kind (mirrors WebApi/Models/ParsedSong.cs). */
export type BarKind = 'bar' | 'repeat-start' | 'repeat-end';

export interface BarToken {
  kind: BarKind;
  charIndex: number;
}

export interface ParsedLine {
  text: string;
  chords: ChordToken[];
  annotations: Annotation[];
  bars?: BarToken[];
}

export interface ParsedSection {
  label: string | null;
  type: 'verse' | 'chorus' | 'bridge' | 'other';
  lines: ParsedLine[];
}

export interface ParsedHeader {
  title?: string;
  artist?: string;
  key?: string;
  capo?: string;
  time?: string;
  tag?: string;
  meta: { name: string; value: string }[];
}

export interface ParsedSong {
  header: ParsedHeader | null;
  sections: ParsedSection[];
  warnings: string[];
}

export interface SongDetailResponse {
  song: SongDetail;
  parsed: ParsedSong;
}

// ---------- Offline sync (MT-04) ----------

/** GET /api/sync payload — the whole library for the IndexedDB cache (see SyncDtos.cs). */
export interface SyncPayload {
  serverTime: string;
  /** Changed songs (all on the first sync), full detail with chordProText. */
  songs: SongDetail[];
  /** Every song id on the server — the client prunes cached songs not in this list. */
  songIds: string[];
  /** Changed playlists (all on the first sync), with their songs in position order. */
  playlists: PlaylistDetail[];
  playlistIds: string[];
  collections: Collection[];
}

// ---------- Restore push (MT-05) ----------

/**
 * One cached song uploaded by POST /api/sync/push. `clientId` is the cache id — the server
 * issues its own id and returns the mapping (SyncDtos.cs), so the cache re-keys and pruning
 * never deletes a row the push landed. Key/capo/tempo keep the device metadata the chart
 * headers may not carry; a header directive still wins server-side.
 */
export interface PushSong {
  clientId: string;
  title: string | null;
  artist: string | null;
  album: string | null;
  year: number | null;
  key: string | null;
  genre: string | null;
  tempo: number | null;
  capoFret: number;
  chordProText: string;
  sourceUrl: string | null;
}

/** A cached playlist; `songClientIds` are cache ids in position order (server-mapped on push). */
export interface PushPlaylist {
  clientId: string;
  name: string;
  description: string | null;
  isSetlist: boolean;
  gigDate: string | null;
  songClientIds: string[];
}

export interface PushLibraryRequest {
  songs?: PushSong[];
  playlists?: PushPlaylist[];
}

export interface PushSongResult {
  clientId: string;
  /** The server's id for the row (newly created, or the dedup match). */
  id: string;
  created: boolean;
  /** null when the row landed; the server failure code (e.g. "not-chordpro") when it did not. */
  error: string | null;
}

export interface PushPlaylistResult {
  clientId: string;
  id: string;
  created: boolean;
  error: string | null;
}

export interface PushLibraryResponse {
  serverTime: string;
  songs: PushSongResult[];
  playlists: PushPlaylistResult[];
}

/** ST-46 bug-56: a source file (pdf/png/jpg) attached to a song, stored as bytes on the server. */
export interface SongSourceFile {
  id: string;
  songId: string;
  fileName: string;
  contentType: string;
  sizeBytes: number;
  createdAt: string;
}

// ---------- Import (ST-13) ----------

export interface ImportRequest {
  chordProText: string;
  title?: string;
  artist?: string;
  // ST-53: album override (MusicBrainz's release title IS the album); the metadata review job fills it in later when unknown.
  album?: string;
  playlistId?: string;
  sourceUrl?: string;
}

export interface ImportResponse {
  song: SongDetail;
  parsed: ParsedSong;
  /** ST-49: true when the song already existed (same title + artist + content) — nothing was created. */
  duplicate?: boolean;
}

// ---------- LLM settings (ST-30) ----------

/** One stored settings row, masked — the API key is never returned in full. */
export interface LlmSettingsView {
  baseUrl: string | null;
  model: string | null;
  keySet: boolean;
  maxTokens: number;
}

/** GET /api/settings/llm — both stored rows plus the merged effective settings. */
export interface LlmSettingsResponse {
  isAdmin: boolean;
  global: LlmSettingsView | null;
  mine: LlmSettingsView | null;
  effective: LlmSettingsView & { source: 'user' | 'global' | 'env' | 'none' };
}

/** PUT /api/settings/llm/mine and /global — empty fields clear that level. */
export interface LlmSettingsUpdateRequest {
  baseUrl?: string;
  model?: string;
  apiKey?: string;
  maxTokens?: number;
}

/** POST /api/settings/llm/test — probe result (200 even on failure, ok:false). */
export interface LlmTestResult {
  ok: boolean;
  models?: string[];
  error?: string;
}

// ---------- Chord-page conversion (ST-14, ST-33) ----------

export type ConversionDraftStatus = 'queued' | 'running' | 'succeeded' | 'failed';

export interface ConversionDraft {
  id: string;
  status: ConversionDraftStatus;
  chordProText?: string;
  error?: string;
  createdAt: string;
  finishedAt?: string;
  savedSongId?: string;
}

/** ST-33: queue-row summary from GET /api/import/convert (light — no chordProText). */
export interface ConversionDraftListItem {
  id: string;
  status: ConversionDraftStatus;
  /** Legacy: a source URL on drafts from the old fetch-based path (new drafts are paste-only). */
  inputUrl?: string | null;
  /** First line of the pasted text, capped at 80 chars. */
  inputPreview?: string | null;
  /** ST-35 E1: title/artist extracted from the converted ChordPro header (succeeded drafts). */
  extractedTitle?: string | null;
  extractedArtist?: string | null;
  /** ST-35 E4: true when title+artist already exist in the library (duplicate signal). */
  matchesLibrary?: boolean | null;
  /** Failure reason when status is "failed". */
  error?: string | null;
  createdAt: string;
  finishedAt?: string | null;
  savedSongId?: string | null;
}

/** ST-33: server→client status transition on the shared hub (per user). */
export interface ConversionProgressEvent {
  id: string;
  status: ConversionDraftStatus;
  chars: number;
  elapsedSeconds: number;
  /** Rises to 2 on retry — clients reset their preview buffer when it does. */
  attempt: number;
}

/** ST-33: server→client streamed LLM output chunk (append to the preview buffer). */
export interface ConversionChunkEvent {
  id: string;
  text: string;
}

// ---------- MusicBrainz (ST-12) ----------

export interface MusicBrainzResult {
  mbid: string;
  title: string;
  artist: string | null;
  album: string | null;
  year: number | null;
  score: number | null;
}

export interface MusicBrainzSearchResponse {
  results: MusicBrainzResult[];
}

// ---------- Songsterr + wishlist (ST-44) ----------

export interface SongsterrTrack {
  instrument: string;
  difficulty: number | null;
}

export interface SongsterrResult {
  songId: number;
  title: string;
  artist: string | null;
  hasChords: boolean;
  tracks: SongsterrTrack[];
}

export interface SongsterrSearchResponse {
  results: SongsterrResult[];
}

export type WishlistStatus = 'waiting' | 'found' | 'converted';

export interface WishlistItem {
  id: string;
  title: string;
  artist: string | null;
  sourceUrl: string | null;
  status: WishlistStatus;
  convertedSongId: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface CreateWishlistRequest {
  title: string;
  artist?: string | null;
  sourceUrl?: string | null;
}

export interface UpdateWishlistRequest {
  status?: WishlistStatus;
  convertedSongId?: string | null;
}

// ---------- Auth (ST-05, ST-26) ----------

export interface ChangePasswordRequest {
  currentPassword: string;
  newPassword: string;
}

export interface UpdateMeRequest {
  displayName: string;
}

// ---------- Playlists ----------

export interface Playlist {
  id: string;
  name: string;
  description: string | null;
  collectionId: string | null;
  isSetlist: boolean;
  /** Bug report #99: the gig this playlist is for ("yyyy-MM-dd"), when set. */
  gigDate: string | null;
  songCount: number;
  createdAt: string;
  updatedAt: string;
  /** ST-106: setlist tags (the collection retirement — several per setlist). */
  tags?: string[] | null;
}

export interface PlaylistDetail extends Playlist {
  songs: Song[];
}

/**
 * ST-87: a live follow session on the server (GET /api/sessions). The app is one
 * server on the network, so every session listed here is joinable from any device.
 */
export interface ActiveSession {
  hostId: string;
  hostName: string;
  /** What the host is looking at: 'song' (page/player) or 'setlist' (setlist player). */
  kind: 'song' | 'setlist';
  songId: string | null;
  songTitle: string | null;
  songArtist: string | null;
  playlistId: string | null;
  songIndex: number | null;
  followers: number;
  startedAt: string;
}

/** ST-87: the hub's follow-state payload (broadcast on publish / join / leave). */
export interface FollowStatePayload {
  hostId: string;
  hostName: string;
  kind: 'song' | 'setlist';
  songId: string | null;
  songTitle: string | null;
  songArtist: string | null;
  playlistId: string | null;
  songIndex: number | null;
  /** Monotonic publish counter — clients order/dedupe view updates with it. */
  seq: number;
  followers: number;
}

export interface CreatePlaylistRequest {
  name: string;
  description?: string;
  collectionId?: string;
  isSetlist?: boolean;
  /** Bug report #99: "yyyy-MM-dd"; "" clears. */
  gigDate?: string;
  /** ST-106: setlist tags — a sent list replaces the setlist's tags. */
  tags?: string[];
}

export type UpdatePlaylistRequest = Partial<CreatePlaylistRequest>;

export interface AddSongRequest {
  songId: string;
  position?: number;
}

export interface ReorderRequest {
  songIds: string[];
}

export interface AddSongResponse {
  id: string;
  songId: string;
  position: number;
}

// ---------- Collections ----------

export interface Collection {
  id: string;
  name: string;
  description: string | null;
  category: string;
  playlistCount: number;
  createdAt: string;
}

export interface CollectionDetail extends Collection {
  playlists: Playlist[];
}

export interface CreateCollectionRequest {
  name: string;
  description?: string;
  category?: string;
}

export type UpdateCollectionRequest = Partial<CreateCollectionRequest>;

// ---------- App info (ST-31) ----------

/** GET /api/info — service name, version, and the raw CHANGELOG.md markdown. */
export interface InfoResponse {
  service: string;
  version: string;
  changelog: string;
}

// ---------- Errors ----------

export interface ApiErrorBody {
  error: {
    code: string;
    message: string;
    fields?: Record<string, string | null>;
  };
}

/**
 * Thrown by the API client for any non-2xx response (and network failures).
 * `status` is 0 for network errors. `code`/`fields` come from the API error body
 * when present (the { error: { code, message, fields? } } shape).
 */
export class ApiError extends Error {
  readonly status: number;
  readonly code: string;
  readonly fields?: Record<string, string | null>;

  constructor(
    status: number,
    code: string,
    message: string,
    fields?: Record<string, string | null>,
  ) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
    this.code = code;
    this.fields = fields;
  }
}

// ---------- Bug reports (ST-37 D1) ----------

/** One submitted bug report from /data/bug_reports.md. */
export interface BugReportEntry {
  id: string;
  timestamp: string;
  user: string;
  text: string;
  /** ST-39 D2: new / resolved / waiting (waiting for a question to be answered). */
  status: 'new' | 'resolved' | 'waiting';
  /** App version the bug was resolved in (status === 'resolved'). */
  resolvedIn?: string | null;
  /** ST-40 D2: short title added while processing — reports with a title collapse to it. */
  title?: string | null;
}
