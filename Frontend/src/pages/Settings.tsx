import { useEffect, useId, useState } from 'react';
import ReactMarkdown from 'react-markdown';
import {
  apiAppVersion,
  apiAvatarDelete,
  apiAvatarSet,
  apiBugReportStatus,
  apiBugReports,
  apiChangePassword,
  apiInfo,
  apiLlmSettings,
  apiLlmSettingsGlobal,
  apiLlmSettingsMine,
  apiLlmTest,
  apiPlaylists,
  apiSongCount,
  apiUpdateMe,
  appDownloadUrl,
  avatarUrl,
} from '../api/client';
import type { AppVersionInfo } from '../api/client';
import { ApiError } from '../api/types';
import { clearServerUrl, getServerUrl, isNativeShell } from '../api/server';
import { initials } from '../utils/initials';
import type { BugReportEntry, InfoResponse, LlmSettingsResponse, LlmSettingsUpdateRequest, LlmSettingsView, LlmTestResult } from '../api/types';
import { useAuthStore } from '../stores/auth';
import { useUiStore } from '../stores/ui';
import type { DiagramType, FontSize, Theme } from '../stores/ui';
import { LogoMark, LOGO_IDS, LOGO_LABELS } from '../components/Logos';
import { TextField } from '../components/TextField';
import { useToastStore } from '../stores/toast';
import ui from '../components/ui.module.css';
import styles from './Settings.module.css';

/**
 * Profile / settings (ST-26): account (display name, stats), appearance
 * (theme, font size, diagram default — all instant + persisted), password
 * change, sign out.
 */
export default function Settings() {
  const pushToast = useToastStore((s) => s.push);
  const user = useAuthStore((s) => s.user);

  const theme = useUiStore((s) => s.theme);
  const fontSize = useUiStore((s) => s.fontSize);
  const diagramType = useUiStore((s) => s.diagramType);
  const setTheme = useUiStore((s) => s.setTheme);
  const setFontSize = useUiStore((s) => s.setFontSize);
  const setDiagramType = useUiStore((s) => s.setDiagramType);
  const logo = useUiStore((s) => s.logo);
  const setLogo = useUiStore((s) => s.setLogo);

  const [displayName, setDisplayName] = useState(user?.displayName ?? '');
  const [nameSaving, setNameSaving] = useState(false);

  const [stats, setStats] = useState<{ songs: number; playlists: number } | null>(null);

  // ST-31: app version + changelog (About section).
  const [info, setInfo] = useState<InfoResponse | null>(null);

  // MT-02: the published Android APK for the About download link — hidden when
  // no build is published. The version comes from GET /api/app/version (the
  // download's REAL version — the About badge above is the web version).
  const [apk, setApk] = useState<AppVersionInfo | null>(null);

  // ST-30: LLM settings (global default + per-user override).
  const [llm, setLlm] = useState<LlmSettingsResponse | null>(null);
  // ST-36 B2: one form at a time — toggle between your settings and the band default.
  const [llmTab, setLlmTab] = useState<'mine' | 'global'>('mine');

  // ST-36 B3: one tab per section; Change password lives under Account.
  const [tab, setTab] = useState<SettingsTab>('account');

  // ST-37 D1: submitted bug reports (list + resolved status).
  const [bugReports, setBugReports] = useState<BugReportEntry[] | null>(null);
  const [bugBusy, setBugBusy] = useState<string | null>(null);
  // ST-40 D2: which titled reports are expanded (default: collapsed to their title).
  const [expandedBugs, setExpandedBugs] = useState<Record<string, boolean>>({});

  const [currentPassword, setCurrentPassword] = useState('');
  const [newPassword, setNewPassword] = useState('');
  const [confirmPassword, setConfirmPassword] = useState('');
  const [pwFields, setPwFields] = useState<Record<string, string>>({});
  const [pwError, setPwError] = useState<string | null>(null);
  const [pwSaving, setPwSaving] = useState(false);

  useEffect(() => {
    // ST-64: apiSongCount() — apiSongs(…,100).length reported "100 songs" for a bigger library.
    Promise.all([apiSongCount(), apiPlaylists()])
      .then(([counts, playlists]) => setStats({ songs: counts.count, playlists: playlists.length }))
      .catch(() => setStats(null));
    apiLlmSettings().then(setLlm).catch(() => setLlm(null));
    apiInfo().then(setInfo).catch(() => setInfo(null));
    // MT-02: APK availability + version (never rejects — null on network failure).
    void apiAppVersion().then(setApk);
    // ST-37 D1: the bug report list (visible to everyone).
    apiBugReports().then(setBugReports).catch(() => setBugReports([]));
  }, []);

  // ST-39 D2: set a bug report's status; resolving records the current app version.
  async function setBugStatus(b: BugReportEntry, status: 'new' | 'resolved' | 'waiting') {
    setBugBusy(b.id);
    try {
      const resolvedIn = status === 'resolved' ? info?.version ?? null : null;
      await apiBugReportStatus(b.id, status, resolvedIn ?? undefined);
      setBugReports((list) => (list ? list.map((x) => (x.id === b.id ? { ...x, status, resolvedIn } : x)) : list));
    } catch (e) {
      pushToast(e instanceof Error ? e.message : 'Could not update the report.', 'error');
    } finally {
      setBugBusy(null);
    }
  }

  async function saveName() {
    if (!displayName.trim()) return;
    setNameSaving(true);
    try {
      const me = await apiUpdateMe({ displayName: displayName.trim() });
      useAuthStore.getState().setUser(me);
      setDisplayName(me.displayName ?? '');
      pushToast('Display name updated.');
    } catch (e) {
      pushToast(e instanceof Error ? e.message : 'Could not update the name.', 'error');
    } finally {
      setNameSaving(false);
    }
  }

  // ST-70c: profile image. The upload response carries the new cache-bust version,
  // so the top-bar avatar refreshes as soon as the store user updates.
  const [avatarBusy, setAvatarBusy] = useState(false);
  const [avatarError, setAvatarError] = useState<string | null>(null);

  async function uploadAvatar(file: File) {
    setAvatarBusy(true);
    setAvatarError(null);
    try {
      useAuthStore.getState().setUser(await apiAvatarSet(file));
      pushToast('Profile image updated.');
    } catch (e) {
      setAvatarError(e instanceof ApiError ? e.message : 'Could not upload the image.');
    } finally {
      setAvatarBusy(false);
    }
  }

  async function removeAvatar() {
    setAvatarBusy(true);
    setAvatarError(null);
    try {
      useAuthStore.getState().setUser(await apiAvatarDelete());
      pushToast('Profile image removed.');
    } catch (e) {
      setAvatarError(e instanceof ApiError ? e.message : 'Could not remove the image.');
    } finally {
      setAvatarBusy(false);
    }
  }

  async function changePassword(e: React.FormEvent) {
    e.preventDefault();
    setPwError(null);
    const fields: Record<string, string> = {};
    if (!currentPassword) fields.currentPassword = 'Current password is required.';
    if (newPassword.length < 8) fields.newPassword = 'New password must be at least 8 characters.';
    if (confirmPassword !== newPassword) fields.confirmPassword = 'Passwords do not match.';
    setPwFields(fields);
    if (Object.keys(fields).length > 0) return;

    setPwSaving(true);
    try {
      const res = await apiChangePassword({ currentPassword, newPassword });
      // ST-90 gate 4: the password change rotates the security stamp, which revokes this
      // device's token too — adopt the replacement the endpoint returned, so changing a
      // password does not sign out the device that changed it (the other devices signing out
      // IS the control working).
      if (res.token && res.user) useAuthStore.getState().rotateSession(res.token, res.user);
      pushToast('Password changed.');
      setCurrentPassword('');
      setNewPassword('');
      setConfirmPassword('');
    } catch (e2: unknown) {
      if (e2 instanceof ApiError) {
        setPwError(e2.message);
        if (e2.fields) {
          const f: Record<string, string> = {};
          for (const [k, v] of Object.entries(e2.fields)) if (v) f[k] = v;
          setPwFields(f);
        }
      } else {
        setPwError(e2 instanceof Error ? e2.message : 'Password change failed.');
      }
    } finally {
      setPwSaving(false);
    }
  }

  return (
    <div className={styles.page}>
      {/* ST-36 B3: settings as tabs — one section at a time. */}
      <nav className={styles.tabs} aria-label="Settings sections">
        {SETTINGS_TABS.map((t) => (
          <button
            key={t.id}
            type="button"
            role="tab"
            aria-selected={tab === t.id}
            className={`${styles.tab} ${tab === t.id ? styles.tabActive : ''}`}
            onClick={() => setTab(t.id)}
          >
            {t.label}
          </button>
        ))}
      </nav>

      {tab === 'account' && (
        <>
      <section className={ui.card} aria-label="Account">
        <h2 className={styles.sectionTitle}>Account</h2>
        {/* ST-35 A1: show the login username alongside the email. */}
        <p className={ui.muted}>{[user?.username, user?.email].filter(Boolean).join(' · ') || '—'}</p>

        {/* ST-70c: profile image for the top-bar avatar (PNG/JPG/WebP, 1 MB). */}
        <div className={styles.avatarRow}>
          <span className={styles.avatarPreview} aria-hidden="true">
            {avatarUrl(user) ? (
              <img src={avatarUrl(user)!} alt="" />
            ) : (
              initials(user?.displayName ?? user?.username ?? user?.email ?? '?')
            )}
          </span>
          <div className={styles.avatarActions}>
            <label className={`${ui.btn} ${ui.small}`}>
              {avatarBusy ? 'Saving…' : 'Upload image'}
              <input
                type="file"
                accept="image/png, image/jpeg, image/webp"
                hidden
                onChange={(e) => {
                  const f = e.target.files?.[0];
                  if (f) void uploadAvatar(f);
                  e.target.value = '';
                }}
              />
            </label>
            {user?.avatarVersion && (
              <button type="button" className={`${ui.btn} ${ui.small}`} onClick={() => void removeAvatar()} disabled={avatarBusy}>
                Remove
              </button>
            )}
            <p className={ui.muted}>PNG, JPG or WebP · up to 1 MB · shown in the top bar.</p>
          </div>
        </div>
        {avatarError && (
          <div className={ui.errorBanner} role="alert">
            <span>{avatarError}</span>
          </div>
        )}

        <div className={styles.nameRow}>
          <TextField
            label="Display name"
            value={displayName}
            onChange={setDisplayName}
            placeholder="How should we greet you?"
          />
          <button
            type="button"
            className={`${ui.btn} ${ui.primary}`}
            onClick={() => void saveName()}
            disabled={nameSaving || !displayName.trim()}
          >
            {nameSaving ? 'Saving…' : 'Save'}
          </button>
        </div>
        {stats && (
          <p className={styles.stats}>
            <strong>{stats.songs}</strong> song{stats.songs === 1 ? '' : 's'} in the library ·{' '}
            <strong>{stats.playlists}</strong> playlist{stats.playlists === 1 ? '' : 's'}
          </p>
        )}
      </section>

        {/* ST-36 B1/B3: Change password lives under the Account tab. */}
        <section className={ui.card} aria-label="Change password">
          <h2 className={styles.sectionTitle}>Change password</h2>
          {pwError && (
            <div className={ui.errorBanner} role="alert">
              <span>{pwError}</span>
            </div>
          )}
          <form onSubmit={changePassword} className={styles.pwForm}>
            <TextField
              label="Current password"
              type="password"
              value={currentPassword}
              onChange={(v) => {
                setCurrentPassword(v);
                setPwFields((f) => ({ ...f, currentPassword: '' }));
              }}
              error={pwFields.currentPassword || undefined}
              autoComplete="current-password"
            />
            <TextField
              label="New password"
              type="password"
              value={newPassword}
              onChange={(v) => {
                setNewPassword(v);
                setPwFields((f) => ({ ...f, newPassword: '' }));
              }}
              error={pwFields.newPassword || undefined}
              autoComplete="new-password"
            />
            <TextField
              label="Confirm new password"
              type="password"
              value={confirmPassword}
              onChange={(v) => {
                setConfirmPassword(v);
                setPwFields((f) => ({ ...f, confirmPassword: '' }));
              }}
              error={pwFields.confirmPassword || undefined}
              autoComplete="new-password"
            />
            <button type="submit" className={`${ui.btn} ${ui.primary}`} disabled={pwSaving}>
              {pwSaving ? 'Changing…' : 'Change password'}
            </button>
          </form>
        </section>
      </>
      )}

      {tab === 'appearance' && (
      <section className={ui.card} aria-label="Appearance">
        <h2 className={styles.sectionTitle}>Appearance</h2>

        <div className={styles.prefRow}>
          <span>Theme</span>
          <div className={styles.seg} role="radiogroup" aria-label="Theme">
            {(['dark', 'light'] as Theme[]).map((t) => (
              <button
                key={t}
                type="button"
                role="radio"
                aria-checked={theme === t}
                className={theme === t ? styles.segActive : undefined}
                onClick={() => setTheme(t)}
              >
                {t === 'dark' ? 'Dark' : 'Light'}
              </button>
            ))}
          </div>
        </div>

        <div className={styles.prefRow}>
          <span>Font size</span>
          <div className={styles.seg} role="radiogroup" aria-label="Font size">
            {(['S', 'M', 'L'] as FontSize[]).map((s) => (
              <button
                key={s}
                type="button"
                role="radio"
                aria-checked={fontSize === s}
                className={fontSize === s ? styles.segActive : undefined}
                onClick={() => setFontSize(s)}
              >
                {s}
              </button>
            ))}
          </div>
        </div>

        <div className={styles.prefRow}>
          <span>Chord diagrams</span>
          <div className={styles.seg} role="radiogroup" aria-label="Default diagram type">
            {(['guitar', 'piano'] as DiagramType[]).map((d) => (
              <button
                key={d}
                type="button"
                role="radio"
                aria-checked={diagramType === d}
                className={diagramType === d ? styles.segActive : undefined}
                onClick={() => setDiagramType(d)}
              >
                {d === 'guitar' ? 'Guitar' : 'Piano'}
              </button>
            ))}
          </div>
        </div>

        {/* ST-66: all logo concepts, pickable; the top bar + favicon follow. */}
        <div className={`${styles.prefRow} ${styles.prefRowStack}`}>
          <span>Logo</span>
          <div className={styles.logoGrid} role="radiogroup" aria-label="App logo">
            {LOGO_IDS.map((id) => (
              <button
                key={id}
                type="button"
                role="radio"
                aria-checked={logo === id}
                className={`${styles.logoOption} ${logo === id ? styles.logoOptionActive : ''}`}
                onClick={() => setLogo(id)}
              >
                <LogoMark id={id} size={34} />
                <span>{LOGO_LABELS[id]}</span>
              </button>
            ))}
          </div>
        </div>
      </section>
      )}

      {tab === 'llm' && (
      llm ? (
        <section className={ui.card} aria-label="LLM conversion">
          <h2 className={styles.sectionTitle}>LLM conversion</h2>
          <p className={ui.muted}>
            Used by “Convert in app” on the Import screen. Your own settings win over the band
            default, which wins over the server’s .env file.
          </p>

          {llm.effective.source === 'none' ? (
            <div className={`${styles.testResult} ${styles.testFail}`} role="status">
              Not configured — in-app conversion is off until a base URL is set below.
            </div>
          ) : (
            <p className={styles.llmEffective}>
              In use: <code>{llm.effective.baseUrl}</code> · {llm.effective.model ?? 'default model'}{' '}
              <span className={`${ui.badge} ${styles.sourceBadge}`}>
                {llm.effective.source === 'user' ? 'your settings' : llm.effective.source === 'global' ? 'band default' : '.env file'}
              </span>
            </p>
          )}

          {/* ST-36 B2: switch between the two scopes instead of showing both at once. */}
          <div className={styles.seg} role="tablist" aria-label="Which LLM settings to edit">
            <button
              type="button"
              role="tab"
              aria-selected={llmTab === 'mine'}
              className={llmTab === 'mine' ? styles.segActive : undefined}
              onClick={() => setLlmTab('mine')}
            >
              Your settings
            </button>
            {llm.isAdmin && (
              <button
                type="button"
                role="tab"
                aria-selected={llmTab === 'global'}
                className={llmTab === 'global' ? styles.segActive : undefined}
                onClick={() => setLlmTab('global')}
              >
                Band default
              </button>
            )}
          </div>

          {llmTab === 'mine' || !llm.isAdmin ? (
            <LlmForm
              title="Your LLM settings"
              initial={llm.mine}
              saveFn={apiLlmSettingsMine}
              onSaved={setLlm}
            />
          ) : (
            <LlmForm
              title="Band default (all members)"
              hint="Members without their own settings fall back to this."
              initial={llm.global}
              saveFn={apiLlmSettingsGlobal}
              onSaved={setLlm}
            />
          )}
        </section>
      ) : (
        <p className={ui.muted}>Loading…</p>
      )
      )}

      {/* ST-37 D1: submitted bug reports + resolved status (visible to everyone). */}
      {tab === 'bugs' && (
        <section className={ui.card} aria-label="Bug reports">
          <h2 className={styles.sectionTitle}>Bug reports</h2>
          {bugReports === null ? (
            <p className={ui.muted}>Loading…</p>
          ) : bugReports.length === 0 ? (
            <p className={ui.muted}>No bug reports yet.</p>
          ) : (
            <ul className={styles.bugList}>
              {bugReports.map((b) => {
                const hasTitle = !!b.title;
                // ST-40 D2: reports with a title collapse to it by default — click to expand.
                const expanded = hasTitle ? !!expandedBugs[b.id] : true;
                return (
                  <li
                    key={b.id}
                    className={`${styles.bugItem} ${b.status === 'resolved' ? styles.bugResolved : ''} ${hasTitle ? styles.bugCollapsible : ''}`.trim()}
                    // ST-40 D2: titled cards are click-to-expand — keyboard accessible too.
                    role={hasTitle ? 'button' : undefined}
                    tabIndex={hasTitle ? 0 : undefined}
                    aria-expanded={hasTitle ? expanded : undefined}
                    onClick={() => {
                      if (hasTitle) setExpandedBugs((m) => ({ ...m, [b.id]: !expanded }));
                    }}
                    onKeyDown={(e) => {
                      if (hasTitle && (e.key === 'Enter' || e.key === ' ')) {
                        e.preventDefault();
                        setExpandedBugs((m) => ({ ...m, [b.id]: !expanded }));
                      }
                    }}
                  >
                    <div className={styles.bugHead}>
                      {/* ST-39 D2: smaller date font + status pill (+ version resolved in). */}
                      <span className={styles.bugDate}>{b.timestamp}</span>
                      <strong>{b.user}</strong>
                      <span className={`${styles.bugPill} ${styles[`bugPill_${b.status}`]}`}>
                        {b.status === 'waiting' ? 'waiting for a question' : b.status}
                      </span>
                      {b.status === 'resolved' && b.resolvedIn && (
                        <span className={ui.muted}>in v{b.resolvedIn}</span>
                      )}
                      {/* ST-40 D1: no status dropdown — a Reopen button in the top right. */}
                      {b.status !== 'new' && (
                        <button
                          type="button"
                          className={`${ui.btn} ${ui.small} ${styles.bugReopen}`}
                          disabled={bugBusy !== null}
                          onClick={(e) => {
                            e.stopPropagation();
                            void setBugStatus(b, 'new');
                          }}
                        >
                          Reopen
                        </button>
                      )}
                    </div>
                    {hasTitle && <p className={styles.bugTitle}>{b.title}</p>}
                    {expanded && <p className={styles.bugText}>{b.text}</p>}
                  </li>
                );
              })}
            </ul>
          )}
        </section>
      )}

      {/* ST-36 B1: the Session section is gone — sign out lives in the header. */}

      {tab === 'about' && (
      info ? (
        <section className={ui.card} aria-label="About">
          <h2 className={styles.sectionTitle}>About</h2>
          <p className={styles.versionLine}>
            AhChord{' '}
            <span className={`${ui.badge} ${ui.badgeAccent}`}>v{info.version}</span>
          </p>
          {/* MT-02: Android app — install over the LAN (phone & Shield Pro). Hidden when no build is published. */}
          {apk && (
            <div className={styles.androidDownload}>
              <p className={styles.versionLine}>
                Android app{' '}
                <span className={`${ui.badge} ${ui.badgeAccent}`}>v{apk.versionName}</span>
              </p>
              <a className={`${ui.btn} ${styles.linkBtn}`} href={appDownloadUrl()}>
                Download the Android app (.apk)
              </a>
              <p className={ui.muted}>
                Install on an Android phone or TV from this network — this download is app version{' '}
                {apk.versionName} (build {apk.versionCode})
                {apk.sizeBytes ? ` · ${formatApkSize(apk.sizeBytes)}` : ''}
                {apk.publishedAt ? ` · published ${new Date(apk.publishedAt).toLocaleDateString()}` : ''}.
              </p>
            </div>
          )}
          {/* MT-02: the standalone shell points at one server — show it, allow switching. */}
          {isNativeShell() && (
            <div className={styles.androidDownload}>
              <p>
                Server: <strong>{getServerUrl()}</strong>
              </p>
              <button
                className={`${ui.btn} ${styles.linkBtn}`}
                onClick={() => {
                  clearServerUrl();
                  window.location.assign('/');
                }}
              >
                Change server
              </button>
            </div>
          )}
          {info.changelog ? (
            <div className={styles.changelog}>
              <ReactMarkdown>{info.changelog}</ReactMarkdown>
            </div>
          ) : (
            <p className={ui.muted}>Changelog unavailable.</p>
          )}
        </section>
      ) : (
        <p className={ui.muted}>Loading…</p>
      )
      )}
    </div>
  );
}

/** MT-02: human-readable APK size for the About download line. */
function formatApkSize(bytes: number): string {
  return bytes >= 1024 * 1024 ? `${(bytes / (1024 * 1024)).toFixed(1)} MB` : `${Math.round(bytes / 1024)} KB`;
}

/** ST-36 B3 / ST-37 D1: the settings tabs — one per section, Change password under Account. */
type SettingsTab = 'account' | 'appearance' | 'llm' | 'bugs' | 'about';

const SETTINGS_TABS: { id: SettingsTab; label: string }[] = [
  { id: 'account', label: 'Account' },
  { id: 'appearance', label: 'Appearance' },
  { id: 'llm', label: 'LLM conversion' },
  { id: 'bugs', label: 'Bug reports' },
  { id: 'about', label: 'About' },
];

/**
 * ST-30: one LLM settings form (used for "your settings" and, by admins, the band default).
 * The API key is masked server-side: leaving it blank keeps what is stored; "Remove key" clears it.
 */
function LlmForm({
  title,
  hint,
  initial,
  saveFn,
  onSaved,
}: {
  title: string;
  hint?: string;
  initial: LlmSettingsView | null;
  saveFn: (req: LlmSettingsUpdateRequest) => Promise<LlmSettingsResponse>;
  onSaved: (res: LlmSettingsResponse) => void;
}) {
  const pushToast = useToastStore((s) => s.push);

  const [baseUrl, setBaseUrl] = useState(initial?.baseUrl ?? '');
  const [model, setModel] = useState(initial?.model ?? '');
  const [apiKey, setApiKey] = useState(''); // never pre-filled — the server only reports keySet
  const [maxTokens, setMaxTokens] = useState(
    initial && initial.maxTokens > 0 ? String(initial.maxTokens) : ''
  );
  const [saving, setSaving] = useState(false);
  const [testing, setTesting] = useState(false);
  const [testResult, setTestResult] = useState<LlmTestResult | null>(null);

  // Models the server reported (from Test connection / auto-fetch) — feeds the Model dropdown.
  const [availableModels, setAvailableModels] = useState<string[]>([]);
  const modelListId = useId();

  // Populate the model list from a saved base URL on open — silent; Test is the explicit path.
  // A saved model the server no longer lists is stale: don't keep showing it as if valid.
  useEffect(() => {
    const baseUrl = initial?.baseUrl;
    const savedModel = initial?.model?.trim();
    if (!baseUrl) return;
    apiLlmTest({ baseUrl })
      .then((res) => {
        if (res.ok && res.models?.length) {
          setAvailableModels(res.models);
          if (savedModel && !res.models.includes(savedModel)) setModel('');
        }
      })
      .catch(() => {});
  }, [initial?.baseUrl, initial?.model]);

  function buildBody(removeKey: boolean): LlmSettingsUpdateRequest {
    return {
      baseUrl: baseUrl.trim() || undefined,
      model: model.trim() || undefined,
      // blank input = keep the stored key; explicit remove sends "" (the server distinguishes).
      apiKey: removeKey ? '' : apiKey.trim() || undefined,
      maxTokens: maxTokens.trim() === '' ? undefined : Number(maxTokens),
    };
  }

  async function save(removeKey = false) {
    setSaving(true);
    setTestResult(null);
    try {
      const res = await saveFn(buildBody(removeKey));
      onSaved(res);
      // A saved key is not echoed back — reset the field so it doesn't look editable.
      if (apiKey.trim() || removeKey) setApiKey('');
      pushToast(`${title} saved.`);
    } catch (e) {
      pushToast(e instanceof Error ? e.message : 'Could not save LLM settings.', 'error');
    } finally {
      setSaving(false);
    }
  }

  async function test() {
    if (!baseUrl.trim()) return;
    setTesting(true);
    setTestResult(null);
    try {
      const res = await apiLlmTest({
        baseUrl: baseUrl.trim(),
        model: model.trim() || undefined,
        apiKey: apiKey.trim() || undefined,
      });
      setTestResult(res);
      if (res.ok) setAvailableModels(res.models ?? []);
    } catch (e) {
      setTestResult({ ok: false, error: e instanceof Error ? e.message : 'The test failed.' });
    } finally {
      setTesting(false);
    }
  }

  return (
    <div className={styles.llmForm}>
      <h3 className={styles.llmFormTitle}>{title}</h3>
      {hint && <p className={ui.muted}>{hint}</p>}

      <TextField
        label="Base URL"
        value={baseUrl}
        onChange={(v) => { setBaseUrl(v); setTestResult(null); }}
        placeholder="http://127.0.0.1:1234 — /v1 is added if missing"
        autoComplete="off"
      />
      <TextField
        label="Model"
        value={model}
        onChange={(v) => { setModel(v); setTestResult(null); }}
        placeholder="Pick from the list, or type any model id"
        autoComplete="off"
        listId={availableModels.length > 0 ? modelListId : undefined}
      />
      {availableModels.length > 0 && (
        <>
          <datalist id={modelListId}>
            {availableModels.map((m) => (
              <option key={m} value={m} />
            ))}
          </datalist>
          {/* Visible list — native datalist popups are easy to miss. */}
          <div className={styles.modelChips} aria-label="Models available on this server">
            {availableModels.map((m) => (
              <button
                key={m}
                type="button"
                title={`Use ${m}`}
                className={`${ui.btn} ${styles.modelChip} ${model === m ? styles.chipActive : ''}`}
                onClick={() => setModel(m)}
              >
                {m}
              </button>
            ))}
          </div>
        </>
      )}
      <div className={styles.keyRow}>
        <TextField
          label={`API key${initial?.keySet ? ' (set — blank keeps it)' : ''}`}
          type="password"
          value={apiKey}
          onChange={(v) => { setApiKey(v); setTestResult(null); }}
          placeholder={initial?.keySet ? '•••• stored' : 'not-needed for local servers'}
          autoComplete="new-password"
        />
        {initial?.keySet && (
          <button type="button" className={`${ui.btn} ${styles.linkBtn}`} onClick={() => void save(true)}>
            Remove key
          </button>
        )}
      </div>
      <TextField
        label="Max tokens (optional)"
        value={maxTokens}
        onChange={(v) => { setMaxTokens(v); setTestResult(null); }}
        placeholder="4096"
        inputMode="numeric"
        autoComplete="off"
      />

      <div className={styles.llmActions}>
        <button type="button" className={`${ui.btn} ${ui.primary}`} onClick={() => void save()} disabled={saving}>
          {saving ? 'Saving…' : 'Save'}
        </button>
        <button type="button" className={ui.btn} onClick={() => void test()} disabled={testing || !baseUrl.trim()}>
          {testing ? 'Testing…' : 'Test connection'}
        </button>
      </div>

      {testResult && (
        testResult.ok ? (
          <p className={`${styles.testResult} ${styles.testOk}`} role="status">
            ✓ Reachable{testResult.models && testResult.models.length > 0
              ? ` — models: ${testResult.models.slice(0, 4).join(', ')}${testResult.models.length > 4 ? '…' : ''}`
              : ''}
          </p>
        ) : (
          <p className={`${styles.testResult} ${styles.testFail}`} role="alert">
            {testResult.error ?? 'The test failed.'}
          </p>
        )
      )}
    </div>
  );
}
