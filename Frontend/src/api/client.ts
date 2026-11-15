import type {
  ActiveSession,
  AddSongRequest,
  AddSongResponse,
  ApiErrorBody,
  AuthResponse,
  AuthUser,
  BugReportEntry,
  ChangePasswordRequest,
  Collection,
  CollectionDetail,
  CreateCollectionRequest,
  CreatePlaylistRequest,
  CreateSongRequest,
  CreateWishlistRequest,
  ImportRequest,
  ImportResponse,
  InfoResponse,
  MetadataProposal,
  MetadataScanProgress,
  LoginRequest,
  RecentSong,
  MusicBrainzSearchResponse,
  Playlist,
  PlaylistDetail,
  ProposalGroup,
  PushLibraryRequest,
  PushLibraryResponse,
  ReorderRequest,
  RegisterRequest,
  Song,
  SongDetail,
  SongDetailResponse,
  SongFacets,
  SongGroup,
  SongSourceFile,
  SyncPayload,
  SongsterrSearchResponse,
  TagRow,
  DetectedKey,
  LlmSettingsResponse,
  LlmSettingsUpdateRequest,
  LlmTestResult,
  ConversionDraft,
  ConversionDraftListItem,
  UpdateCollectionRequest,
  UpdateMeRequest,
  UpdatePlaylistRequest,
  UpdateSongRequest,
  UpdateWishlistRequest,
  WishlistItem,
} from './types';
import { ApiError } from './types';
import { useAuthStore } from '../stores/auth';
import { apiBase } from './server';

// MT-02: the base is resolved per request (server.ts) — same-origin on the web,
// the user-entered server address inside the standalone Capacitor shell.

interface RequestOptions {
  method?: string;
  body?: unknown;
  headers?: Record<string, string>;
}

/**
 * Core fetch wrapper.
 * - Base from apiBase() (VITE_API_BASE / relative on the web, the saved
 *   server address in the standalone shell — see server.ts).
 * - JSON in/out.
 * - Attaches `Authorization: Bearer <token>` from the auth store when present.
 * - Any non-2xx response is normalized into a typed ApiError.
 * - A 401 on an *authenticated* request (token present) clears the token; the
 *   ProtectedRoute watches the token and redirects to /login. A 401 with no token
 *   (e.g. bad login credentials) is surfaced as an ApiError for the UI to display.
 */
async function request<T>(path: string, options: RequestOptions = {}): Promise<T> {
  const { method = 'GET', body, headers = {} } = options;

  const finalHeaders: Record<string, string> = { ...headers };
  const token = useAuthStore.getState().token;
  if (token) {
    finalHeaders['Authorization'] = `Bearer ${token}`;
  }
  // FormData sets its own multipart Content-Type (with boundary) — don't override it.
  const isForm = body instanceof FormData;
  if (body !== undefined && !isForm) {
    finalHeaders['Content-Type'] = 'application/json';
  }

  let res: Response;
  try {
    res = await fetch(`${apiBase()}${path}`, {
      method,
      headers: finalHeaders,
      body: body === undefined ? undefined : isForm ? (body as FormData) : JSON.stringify(body),
    });
  } catch {
    throw new ApiError(0, 'network-error', 'Network request failed.');
  }

  const text = await res.text();
  let data: unknown = null;
  if (text) {
    try {
      data = JSON.parse(text);
    } catch {
      data = text;
    }
  }

  if (!res.ok) throw await toApiError(res, text);

  return data as T;
}

/**
 * Normalize a non-2xx response into a typed ApiError (shared by the JSON and blob fetches).
 * A Response body can only be consumed once — pass the already-read `bodyText` when the
 * caller read it first (request() does), otherwise this reads it.
 */
async function toApiError(res: Response, bodyText?: string): Promise<ApiError> {
  const text = bodyText ?? (await res.text());
  let data: unknown = null;
  if (text) {
    try {
      data = JSON.parse(text);
    } catch {
      data = text;
    }
  }

  const errBody = data as Partial<ApiErrorBody> | null;
  const code = errBody?.error?.code ?? 'http-error';
  const message = errBody?.error?.message ?? `Request failed (${res.status})`;
  const fields = errBody?.error?.fields;

  if (res.status === 401 && useAuthStore.getState().token) {
    useAuthStore.getState().logout();
  }

  return new ApiError(res.status, code, message, fields);
}

// ---------- Auth endpoints ----------

export function apiLogin(req: LoginRequest): Promise<AuthResponse> {
  return request<AuthResponse>('/auth/login', { method: 'POST', body: req });
}

export function apiRegister(req: RegisterRequest): Promise<AuthResponse> {
  return request<AuthResponse>('/auth/register', { method: 'POST', body: req });
}

export function apiMe(): Promise<AuthUser> {
  return request<AuthUser>('/auth/me');
}

// --- ST-70c: profile image (avatar) -----------------------------------------

/** Upload/replace the avatar (multipart PUT; the server validates type + size). */
export function apiAvatarSet(file: File): Promise<AuthUser> {
  const fd = new FormData();
  fd.append('file', file);
  return request<AuthUser>('/auth/me/avatar', { method: 'PUT', body: fd });
}

export function apiAvatarDelete(): Promise<AuthUser> {
  return request<AuthUser>('/auth/me/avatar', { method: 'DELETE' });
}

/**
 * URL for the avatar <img>, null when the user has no image. The ?v=<ticks> stamp
 * comes from the server (AvatarUpdatedAt), so a new upload busts the browser cache;
 * the GET is anonymous by design (<img> can't send the Bearer header).
 */
export function avatarUrl(user: AuthUser | null | undefined): string | null {
  if (!user?.id || !user.avatarVersion) return null;
  return `${apiBase()}/users/${user.id}/avatar?v=${user.avatarVersion}`;
}

/** Gate 4 (ST-90): with JWT_STAMP_CHECK on, changing the password revokes the token that made
 *  the call, so the endpoint answers with a replacement for this device. Older deployments
 *  return only `message` — both fields are optional. */
export function apiChangePassword(
  req: ChangePasswordRequest,
): Promise<{ message: string; token?: string; expiresIn?: number; user?: AuthUser }> {
  return request('/auth/change-password', { method: 'POST', body: req });
}

// ST-26: update the account display name.
export function apiUpdateMe(req: UpdateMeRequest): Promise<AuthUser> {
  return request<AuthUser>('/auth/me', { method: 'PATCH', body: req });
}

// ---------- Songs (ST-06/07/08/09) ----------

function qs(params: Record<string, string | number | string[] | undefined>): string {
  const parts: string[] = [];
  for (const [k, v] of Object.entries(params)) {
    if (Array.isArray(v)) {
      // ST-106: repeated keys (?tag=a&tag=b) — the server binds string[] filters this way.
      for (const item of v) {
        if (item !== '') parts.push(`${encodeURIComponent(k)}=${encodeURIComponent(item)}`);
      }
    } else if (v !== undefined && v !== '')
      parts.push(`${encodeURIComponent(k)}=${encodeURIComponent(String(v))}`);
  }
  return parts.length ? `?${parts.join('&')}` : '';
}

/**
 * ST-35 D4/D5: `artist` / `genre` / `decade` feed the right-hand filter panel.
 * ST-106: `artist` is an exact tag-name match across every artist of a song,
 * `tags` repeat (AND-narrowing, the collection-style filter).
 */
export function apiSongs(
  search?: string,
  page = 1,
  pageSize = 20,
  artist?: string,
  genre?: string,
  decade?: number,
  tags?: string[],
): Promise<Song[]> {
  // ST-112: the repeated query key is `tag` (the server binds string[] tag).
  return request<Song[]>(
    `/songs${qs({ search, page, pageSize, artist, genre, decade, tag: tags })}`,
  );
}

/** ST-35 D4: distinct genres + decades for the filter dropdowns. */
export function apiSongFacets(): Promise<SongFacets> {
  return request<SongFacets>(`/songs/facets`);
}

/**
 * ST-35 F1: songs grouped by title — one row per song with a version count + latest.
 * ST-106: groups collapse by title only, so multi-artist versions of one song sit together.
 */
export function apiSongGroups(
  search?: string,
  page = 1,
  pageSize = 20,
  artist?: string,
  genre?: string,
  decade?: number,
  tags?: string[],
): Promise<SongGroup[]> {
  // ST-112: the repeated query key is `tag` (the server binds string[] tag).
  return request<SongGroup[]>(
    `/songs${qs({ search, page, pageSize, artist, genre, decade, tag: tags, grouped: 'true' })}`,
  );
}

/** ST-35 F1: every version of a song (same title+artist), oldest first. */
export function apiSongVersions(id: string): Promise<Song[]> {
  return request<Song[]>(`/songs/${id}/versions`);
}

/**
 * ST-106: GET /api/tags — autocomplete + the tag browser. `kind` is "artist" |
 * "genre" | "tag" (omit = all kinds), `q` is a case-insensitive substring.
 */
export function apiTags(kind?: 'artist' | 'genre' | 'tag', q?: string): Promise<TagRow[]> {
  return request<TagRow[]>(`/tags${qs({ kind, q })}`);
}

/** ST-106: re-run the chord analyzer (POST /api/songs/{id}/detect-key). */
export function apiDetectKey(id: string): Promise<DetectedKey> {
  return request<DetectedKey>(`/songs/${id}/detect-key`, { method: 'POST' });
}

/** ST-35 H2: send an in-app bug report (appended to /data/bug_reports.md on the server). */
export function apiBugReport(message: string): Promise<{ ok: boolean }> {
  return request<{ ok: boolean }>('/bug-reports', { method: 'POST', body: { message } });
}

/** ST-35 F3: start a full-library metadata scan (background job). */
export function apiScanMetadata(): Promise<{ started: boolean }> {
  return request<{ started: boolean }>('/songs/metadata-review', { method: 'POST' });
}

/** ST-68: live progress of the metadata scan (see MetadataScanProgress in types). */
export function apiMetadataScanProgress(): Promise<MetadataScanProgress> {
  return request<MetadataScanProgress>('/songs/metadata-review/progress');
}

/** ST-35 F3: the song's pending metadata proposals. */
export function apiSongProposals(id: string): Promise<MetadataProposal[]> {
  return request<MetadataProposal[]>(`/songs/${id}/proposals`);
}

/** ST-35 F3: accept a proposal (applies it to the song). */
export function apiAcceptProposal(songId: string, proposalId: string): Promise<{ ok: boolean }> {
  return request<{ ok: boolean }>(`/songs/${songId}/proposals/${proposalId}/accept`, {
    method: 'POST',
  });
}

/** ST-67: mass-approve — accept every pending proposal for one song. */
export function apiAcceptAllProposals(
  songId: string,
): Promise<{ ok: boolean; applied: number; skipped: number }> {
  return request<{ ok: boolean; applied: number; skipped: number }>(
    `/songs/${songId}/proposals/accept-all`,
    { method: 'POST' },
  );
}

/** ST-35 F3: reject a proposal (no change to the song). */
export function apiRejectProposal(songId: string, proposalId: string): Promise<{ ok: boolean }> {
  return request<{ ok: boolean }>(`/songs/${songId}/proposals/${proposalId}/reject`, {
    method: 'POST',
  });
}

/** ST-69: library-wide pending proposals, grouped by field + proposed value. */
export function apiPendingProposalGroups(): Promise<ProposalGroup[]> {
  return request<ProposalGroup[]>('/songs/proposals/pending');
}

/** ST-70g: pending-proposal count for the top-bar activity box (cheap poll). */
export function apiPendingProposalCount(): Promise<number> {
  return request<{ count: number }>('/songs/proposals/pending/count').then((r) => r.count);
}

/** ST-69: accept every pending proposal matching one field + value (category mass-approve). */
export function apiAcceptProposalCategory(
  field: string,
  value: string,
): Promise<{ ok: boolean; applied: number; skipped: number; songs: number }> {
  return request(`/songs/proposals/accept-category`, { method: 'POST', body: { field, value } });
}

/** ST-71: accept every pending proposal for a field, across all values (field mass-approve). */
export function apiAcceptProposalField(
  field: string,
): Promise<{ ok: boolean; applied: number; skipped: number; songs: number }> {
  return request(`/songs/proposals/accept-field`, { method: 'POST', body: { field } });
}

export function apiSong(id: string, capo?: number): Promise<SongDetailResponse> {
  return request<SongDetailResponse>(`/songs/${id}${qs({ capo })}`);
}

export function apiSongRandom(playlistId?: string): Promise<Song> {
  return request<Song>(`/songs/random${qs({ playlistId })}`);
}

export function apiSongsRecent(limit = 5): Promise<RecentSong[]> {
  return request<RecentSong[]>(`/songs/recent${qs({ limit })}`);
}

/** ST-35 C1: total song count for the dashboard (the list endpoint returns no total). */
export function apiSongCount(): Promise<{ count: number }> {
  return request<{ count: number }>(`/songs/count`);
}

export function apiSongView(id: string): Promise<void> {
  return request<void>(`/songs/${id}/view`, { method: 'POST' });
}

export function apiSongCreate(req: CreateSongRequest): Promise<SongDetail> {
  return request<SongDetail>('/songs', { method: 'POST', body: req });
}

export function apiSongUpdate(id: string, req: UpdateSongRequest): Promise<SongDetail> {
  return request<SongDetail>(`/songs/${id}`, { method: 'PUT', body: req });
}

export function apiSongDelete(id: string): Promise<void> {
  return request<void>(`/songs/${id}`, { method: 'DELETE' });
}

// ---------- Offline sync (MT-04) ----------

/**
 * The offline cache payload. Omit `updatedSince` for the first full sync; afterwards send the
 * serverTime stamp from the previous response to get only the changed rows (+ full id lists).
 */
export function apiSync(updatedSince?: string): Promise<SyncPayload> {
  return request<SyncPayload>(`/sync${qs({ updatedSince })}`);
}

/**
 * MT-05 restore push: upload cache rows the server has never seen (rebuilt/empty server,
 * new pairing) before the client prunes. Per-row outcomes with the clientId → serverId
 * mapping — see offline/sync.ts for the ordering that keeps pruning safe.
 */
export function apiSyncPush(req: PushLibraryRequest): Promise<PushLibraryResponse> {
  return request<PushLibraryResponse>('/sync/push', { method: 'POST', body: req });
}

// ---------- Source files (ST-46 bug-56) ----------

/** A song's attached source files (pdf/png/jpg), oldest first. */
export function apiSongSourceFiles(songId: string): Promise<SongSourceFile[]> {
  return request<SongSourceFile[]>(`/songs/${songId}/source-files`);
}

/** Upload a source file (multipart; the browser sets the multipart Content-Type). */
export function apiUploadSourceFile(songId: string, file: File): Promise<SongSourceFile> {
  const form = new FormData();
  form.append('file', file);
  return request<SongSourceFile>(`/songs/${songId}/source-files`, { method: 'POST', body: form });
}

/** Delete a source file. */
export function apiDeleteSourceFile(id: string): Promise<void> {
  return request<void>(`/source-files/${id}`, { method: 'DELETE' });
}

/**
 * Fetch a source file's raw bytes as a Blob (for inline preview / download).
 * The token goes in the Authorization header — never in the URL.
 */
export async function apiSourceFileBlob(id: string): Promise<Blob> {
  const headers: Record<string, string> = {};
  const token = useAuthStore.getState().token;
  if (token) headers['Authorization'] = `Bearer ${token}`;

  let res: Response;
  try {
    res = await fetch(`${apiBase()}/source-files/${id}`, { headers });
  } catch {
    throw new ApiError(0, 'network-error', 'Network request failed.');
  }
  if (!res.ok) throw await toApiError(res);
  return res.blob();
}

// ---------- Import (ST-13) ----------

export function apiImport(req: ImportRequest): Promise<ImportResponse> {
  return request<ImportResponse>('/songs/import', { method: 'POST', body: req });
}

// ---------- Chord-page conversion (ST-14, ST-33) ----------

export function apiConvertStart(input: {
  content: string;
}): Promise<{ id: string; status: string }> {
  return request('/import/convert', { method: 'POST', body: { content: input.content } });
}

/** ST-33: the caller's conversion queue, newest first. */
export function apiConvertList(): Promise<ConversionDraftListItem[]> {
  return request<ConversionDraftListItem[]>('/import/convert');
}

/** ST-33: cancel (queued) or remove (finished). Running → ApiError 409 in-progress. */
export function apiConvertDelete(id: string): Promise<void> {
  return request<void>(`/import/convert/${id}`, { method: 'DELETE' });
}

/** ST-35 E2: re-enqueue a failed conversion. Non-failed → ApiError 409 not-retryable. */
export function apiConvertRetry(id: string): Promise<{ id: string; status: string }> {
  return request<{ id: string; status: string }>(`/import/convert/${id}/retry`, { method: 'POST' });
}

/** Report (2026-11-12): remove every IMPORTED draft of the caller in one call. */
export function apiConvertClearImported(): Promise<{ ok: boolean; removed: number }> {
  return request('/import/convert/imported', { method: 'DELETE' });
}

export function apiConvertDraft(id: string): Promise<ConversionDraft> {
  return request<ConversionDraft>(`/import/convert/${id}`);
}

export function apiConvertSave(
  id: string,
  req?: { title?: string; artist?: string; playlistId?: string },
): Promise<ImportResponse> {
  return request<ImportResponse>(`/import/convert/${id}/save`, { method: 'POST', body: req ?? {} });
}

/** ST-73: import every converted draft in one pass (the queue's "Import all" button). */
export function apiConvertImportAll(): Promise<{
  ok: boolean;
  imported: number;
  duplicates: number;
  failed: number;
  saved: { id: string; songId: string; duplicate: boolean }[];
  failures: { id: string; title: string | null; error: string }[];
}> {
  return request('/import/convert/import-all', { method: 'POST', body: {} });
}

// ---------- LLM settings (ST-30) ----------

export function apiLlmSettings(): Promise<LlmSettingsResponse> {
  return request<LlmSettingsResponse>('/settings/llm');
}

export function apiLlmSettingsMine(req: LlmSettingsUpdateRequest): Promise<LlmSettingsResponse> {
  return request<LlmSettingsResponse>('/settings/llm/mine', { method: 'PUT', body: req });
}

export function apiLlmSettingsGlobal(req: LlmSettingsUpdateRequest): Promise<LlmSettingsResponse> {
  return request<LlmSettingsResponse>('/settings/llm/global', { method: 'PUT', body: req });
}

export function apiLlmTest(req: {
  baseUrl: string;
  model?: string;
  apiKey?: string;
}): Promise<LlmTestResult> {
  return request<LlmTestResult>('/settings/llm/test', { method: 'POST', body: req });
}

// ---------- MusicBrainz (ST-12) ----------

/** ST-45 search scope (see MusicBrainzEndpoints): "artists" restricts to releases by matching artists. */
export type SearchScope = 'everything' | 'songs' | 'artists' | 'albums';

export function apiMusicBrainzSearch(
  q: string,
  limit = 10,
  scope?: SearchScope,
): Promise<MusicBrainzSearchResponse> {
  return request<MusicBrainzSearchResponse>(`/musicbrainz/search${qs({ q, limit, scope })}`);
}

// ---------- Songsterr + wishlist (ST-44) ----------

export function apiSongsterrSearch(q: string, limit = 10): Promise<SongsterrSearchResponse> {
  return request<SongsterrSearchResponse>(`/songsterr/search${qs({ q, limit })}`);
}

export function apiWishlist(): Promise<WishlistItem[]> {
  return request<WishlistItem[]>('/wishlist');
}

export function apiWishlistCreate(req: CreateWishlistRequest): Promise<WishlistItem> {
  return request<WishlistItem>('/wishlist', { method: 'POST', body: req });
}

export function apiWishlistUpdate(id: string, req: UpdateWishlistRequest): Promise<WishlistItem> {
  return request<WishlistItem>(`/wishlist/${id}`, { method: 'PATCH', body: req });
}

export function apiWishlistDelete(id: string): Promise<void> {
  return request<void>(`/wishlist/${id}`, { method: 'DELETE' });
}

// ---------- Playlists (ST-10) ----------

export function apiPlaylists(
  category?: string,
  search?: string,
  tag?: string,
): Promise<Playlist[]> {
  return request<Playlist[]>(`/playlists${qs({ category, search, tag })}`);
}

export function apiPlaylistCreate(req: CreatePlaylistRequest): Promise<Playlist> {
  return request<Playlist>('/playlists', { method: 'POST', body: req });
}

export function apiPlaylist(id: string): Promise<PlaylistDetail> {
  return request<PlaylistDetail>(`/playlists/${id}`);
}

export function apiPlaylistUpdate(id: string, req: UpdatePlaylistRequest): Promise<Playlist> {
  return request<Playlist>(`/playlists/${id}`, { method: 'PUT', body: req });
}

export function apiPlaylistDelete(id: string): Promise<void> {
  return request<void>(`/playlists/${id}`, { method: 'DELETE' });
}

export function apiPlaylistAddSong(id: string, req: AddSongRequest): Promise<AddSongResponse> {
  return request<AddSongResponse>(`/playlists/${id}/songs`, { method: 'POST', body: req });
}

export function apiPlaylistReorder(id: string, songIds: string[]): Promise<void> {
  return request<void>(`/playlists/${id}/songs/reorder`, {
    method: 'PUT',
    body: { songIds } satisfies ReorderRequest,
  });
}

export function apiPlaylistRemoveSong(id: string, songId: string): Promise<void> {
  return request<void>(`/playlists/${id}/songs/${songId}`, { method: 'DELETE' });
}

/**
 * ST-86: the live band sessions on this server (Start/Join discovery). Every device
 * on the network reaches the same server, so this is "any sessions on the network,
 * same port" — polled by the Start/Join UI.
 */
export function apiActiveSessions(): Promise<ActiveSession[]> {
  return request<ActiveSession[]>('/sessions');
}

// ---------- Collections (ST-11) ----------

export function apiCollections(category?: string): Promise<Collection[]> {
  return request<Collection[]>(`/collections${qs({ category })}`);
}

export function apiCollectionCreate(req: CreateCollectionRequest): Promise<Collection> {
  return request<Collection>('/collections', { method: 'POST', body: req });
}

export function apiCollection(id: string): Promise<CollectionDetail> {
  return request<CollectionDetail>(`/collections/${id}`);
}

export function apiCollectionUpdate(id: string, req: UpdateCollectionRequest): Promise<Collection> {
  return request<Collection>(`/collections/${id}`, { method: 'PUT', body: req });
}

export function apiCollectionDelete(id: string): Promise<void> {
  return request<void>(`/collections/${id}`, { method: 'DELETE' });
}

// ---------- App info (ST-31) ----------

/** Version + changelog for Settings → About. */
export function apiInfo(): Promise<InfoResponse> {
  return request<InfoResponse>('/info');
}

// ---------- Android app download (MT-02) ----------

/** URL of the APK download endpoint, following apiBase() like every other call. */
export function appDownloadUrl(): string {
  return `${apiBase()}/app/download`;
}

/**
 * HEAD probe for Settings → About: is an APK published, and what's its size/date?
 * `available: false` when the server has no build (404); null on network failure.
 */
export async function apiAppDownloadInfo(): Promise<{
  available: boolean;
  sizeBytes?: number;
  lastModified?: string;
} | null> {
  try {
    const res = await fetch(`${apiBase()}/app/download`, { method: 'HEAD' });
    if (res.status === 404) return { available: false };
    if (!res.ok) return null;
    return {
      available: true,
      sizeBytes: Number(res.headers.get('Content-Length') ?? 0),
      lastModified: res.headers.get('Last-Modified') ?? undefined,
    };
  } catch {
    return null; // server unreachable — hide the section rather than show a broken link
  }
}

/** MT-02: GET /api/app/version — the published APK's own version (the number the
 *  download IS; the About badge shows the web version, not this one). */
export interface AppVersionInfo {
  versionCode: number;
  versionName: string;
  sizeBytes: number;
  publishedAt: string;
}

/** The APK's real version for Settings → About. null = no build published
 *  (404) or the server is unreachable — the section hides itself either way. */
export async function apiAppVersion(): Promise<AppVersionInfo | null> {
  try {
    const res = await fetch(`${apiBase()}/app/version`);
    if (!res.ok) return null;
    return (await res.json()) as AppVersionInfo;
  } catch {
    return null;
  }
}

// ---------- Bug reports (ST-37 D1) ----------

/** List submitted bug reports, newest first. */
export function apiBugReports(): Promise<BugReportEntry[]> {
  return request<BugReportEntry[]>('/bug-reports');
}

/** Toggle a bug report's resolved status. */
/** ST-39 D2: set a bug report's status (new / resolved / waiting); resolvedIn = the version it was fixed in. */
export function apiBugReportStatus(
  id: string,
  status: 'new' | 'resolved' | 'waiting',
  resolvedIn?: string,
): Promise<{ ok: boolean }> {
  return request(`/bug-reports/${id}/status`, { method: 'POST', body: { status, resolvedIn } });
}

/** ST-40: update a report's status and/or title in one call (null/undefined = leave as-is). */
export function apiBugReportUpdate(
  id: string,
  fields: { status?: 'new' | 'resolved' | 'waiting'; resolvedIn?: string; title?: string },
): Promise<{ ok: boolean }> {
  return request(`/bug-reports/${id}`, { method: 'POST', body: fields });
}
