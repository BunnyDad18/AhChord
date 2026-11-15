// @vitest-environment jsdom
/**
 * Settings → About, Android app row (James: "the APK download version is not
 * being shown correctly"): the row shows the APK's REAL version from
 * GET /api/app/version — the AhChord badge is the web version, not the APK's.
 */
import { createRoot } from 'react-dom/client';
import { act } from 'react-dom/test-utils';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

vi.mock('../api/client', () => ({
  apiAppVersion: vi.fn(() =>
    Promise.resolve({ versionCode: 25, versionName: '0.4.17', sizeBytes: 5_300_000, publishedAt: '2026-11-09T14:30:00Z' }),
  ),
  apiAvatarDelete: vi.fn(),
  apiAvatarSet: vi.fn(),
  apiBugReports: vi.fn(() => Promise.resolve([])),
  apiBugReportStatus: vi.fn(),
  apiChangePassword: vi.fn(),
  apiInfo: vi.fn(() => Promise.resolve({ service: 'ahchord-webapi', version: '0.3.160', changelog: '' })),
  apiLlmSettings: vi.fn(() => Promise.resolve(null)),
  apiLlmSettingsGlobal: vi.fn(),
  apiLlmSettingsMine: vi.fn(),
  apiLlmTest: vi.fn(),
  apiPlaylists: vi.fn(() => Promise.resolve([])),
  apiSongCount: vi.fn(() => Promise.resolve({ count: 3 })),
  apiUpdateMe: vi.fn(),
  appDownloadUrl: () => 'http://localhost/api/app/download',
  avatarUrl: () => '',
}));

import Settings from './Settings';
import { apiAppVersion } from '../api/client';
import { useAuthStore } from '../stores/auth';

let container: HTMLDivElement | null = null;
let root: ReturnType<typeof createRoot> | null = null;

async function mount() {
  (globalThis as Record<string, unknown>).IS_REACT_ACT_ENVIRONMENT = true;
  container = document.createElement('div');
  document.body.appendChild(container);
  root = createRoot(container);
  await act(async () => {
    root!.render(
      <MemoryRouter>
        <Settings />
      </MemoryRouter>,
    );
  });
  // Resolve the About-tab data fetches.
  await act(async () => {
    await Promise.resolve();
  });
}

async function openAboutTab() {
  const tabBtn = [...container!.querySelectorAll('button')].find((b) => b.textContent === 'About')!;
  await act(async () => {
    tabBtn.dispatchEvent(new window.MouseEvent('click', { bubbles: true }));
  });
}

beforeEach(() => {
  useAuthStore.setState({ user: { id: 'u-1', email: 'a@b.c', displayName: 'Ada' } as never, token: 't' });
});

afterEach(async () => {
  await act(async () => root?.unmount());
  container?.remove();
  container = null;
  root = null;
  vi.clearAllMocks();
});

describe('Settings → About Android app row', () => {
  it('shows the APK version from /api/app/version beside the download', async () => {
    await mount();
    await openAboutTab();
    expect(apiAppVersion).toHaveBeenCalled();
    const text = container!.textContent ?? '';
    expect(text).toContain('Android app');
    expect(text).toContain('v0.4.17');
    expect(text).toContain('app version 0.4.17 (build 25)');
    // The AhChord badge stays the WEB version (not the APK's).
    expect(text).toContain('v0.3.160');
  });

  it('no published build → the Android row is hidden', async () => {
    vi.mocked(apiAppVersion).mockResolvedValue(null);
    await mount();
    await openAboutTab();
    expect(container!.textContent ?? '').not.toContain('Android app');
  });
});
