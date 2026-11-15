/**
 * MT-02: runtime server base for the standalone shell — normalization,
 * web/shell switching, persistence, health probe.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  apiBase,
  checkServer,
  clearServerUrl,
  getServerUrl,
  hubUrl,
  isNativeShell,
  normalizeServerUrl,
  setServerUrl,
} from './server';

const NATIVE = {
  isNativePlatform: () => true,
  Plugins: { AhChord: { setServer: vi.fn(() => Promise.resolve()) } },
};

function asNative() {
  window.Capacitor = NATIVE;
}
function asWeb() {
  delete window.Capacitor;
}

beforeEach(() => {
  localStorage.clear();
  asWeb();
  vi.mocked(NATIVE.Plugins.AhChord!.setServer).mockClear();
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('normalizeServerUrl', () => {
  it('adds http:// when the scheme is omitted and keeps the port', () => {
    expect(normalizeServerUrl('192.168.1.189:5000')).toBe('http://192.168.1.189:5000');
  });
  it('strips trailing slashes and paths — the instance is served at root', () => {
    expect(normalizeServerUrl('http://host:5000/')).toBe('http://host:5000');
    expect(normalizeServerUrl('https://ahchord.example/some/path')).toBe('https://ahchord.example');
  });
  it('accepts https and trims', () => {
    expect(normalizeServerUrl('  https://studio.test ')).toBe('https://studio.test');
  });
  it('MT-02 scoped cleartext: private/LAN hosts default to http, public and .test to https', () => {
    // bare input: scheme chosen by host scope
    expect(normalizeServerUrl('192.168.1.189:5000')).toBe('http://192.168.1.189:5000');
    expect(normalizeServerUrl('10.0.0.9:5000')).toBe('http://10.0.0.9:5000');
    expect(normalizeServerUrl('172.20.1.5:5000')).toBe('http://172.20.1.5:5000');
    expect(normalizeServerUrl('localhost:5000')).toBe('http://localhost:5000');
    expect(normalizeServerUrl('nas:5000')).toBe('http://nas:5000');
    expect(normalizeServerUrl('box.local:5000')).toBe('http://box.local:5000');
    expect(normalizeServerUrl('studio.test')).toBe('https://studio.test');
    expect(normalizeServerUrl('band.example.com')).toBe('https://band.example.com');
    // explicit scheme = user intent, respected as entered
    expect(normalizeServerUrl('http://band.example.com:8080')).toBe('http://band.example.com:8080');
    expect(normalizeServerUrl('http://10.0.0.9:5000')).toBe('http://10.0.0.9:5000');
    expect(normalizeServerUrl('https://192.168.1.10:5000')).toBe('https://192.168.1.10:5000');
  });
  it('rejects blanks, non-http schemes, and junk', () => {
    expect(normalizeServerUrl('')).toBeNull();
    expect(normalizeServerUrl('ftp://host')).toBeNull();
    expect(normalizeServerUrl('not a url !@#')).toBeNull();
  });
});

describe('apiBase / hubUrl', () => {
  it('web build: relative paths, exactly as before MT-02', () => {
    expect(isNativeShell()).toBe(false);
    expect(apiBase()).toBe('/api');
    expect(hubUrl()).toBe('/hubs/session');
  });

  it('web build ignores a stored address (browser keeps same-origin)', () => {
    setServerUrl('http://10.0.0.9:5000');
    expect(apiBase()).toBe('/api');
  });

  it('shell: built from the saved address', () => {
    asNative();
    setServerUrl('http://10.0.0.9:5000');
    expect(apiBase()).toBe('http://10.0.0.9:5000/api');
    expect(hubUrl()).toBe('http://10.0.0.9:5000/hubs/session');
  });

  it('shell before setup: relative (nothing fetched — the gate shows setup first)', () => {
    asNative();
    expect(getServerUrl()).toBeNull();
    expect(apiBase()).toBe('/api');
  });
});

describe('setServerUrl / clearServerUrl', () => {
  it('persists and reports the address to the native plugin', () => {
    asNative();
    setServerUrl('http://10.0.0.9:5000');
    expect(getServerUrl()).toBe('http://10.0.0.9:5000');
    expect(NATIVE.Plugins.AhChord!.setServer).toHaveBeenCalledWith({ url: 'http://10.0.0.9:5000' });
    clearServerUrl();
    expect(getServerUrl()).toBeNull();
  });

  it('survives with no plugin present (web build, older APK)', () => {
    asWeb();
    expect(() => setServerUrl('http://10.0.0.9:5000')).not.toThrow();
  });
});

describe('checkServer', () => {
  it('ok on a healthy 200', async () => {
    const fetchMock = vi.fn(async (_url: string) => ({ ok: true, status: 200 }));
    vi.stubGlobal('fetch', fetchMock);
    await expect(checkServer('host:5000')).resolves.toEqual({ ok: true });
    expect(fetchMock.mock.calls[0][0]).toBe('http://host:5000/api/health');
  });

  it('reports non-2xx as not-an-AhChord server', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve({ ok: false, status: 404 })));
    const res = await checkServer('http://host:5000');
    expect(res.ok).toBe(false);
    expect(!res.ok && res.error).toMatch(/404/);
  });

  it('unreachable address → friendly offline error', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.reject(new TypeError('Failed to fetch'))));
    const res = await checkServer('http://10.255.255.1:5000');
    expect(res.ok).toBe(false);
    expect(!res.ok && res.error).toMatch(/Could not reach/);
  });

  it('bad input is caught before any fetch', async () => {
    const fetchMock = vi.fn();
    vi.stubGlobal('fetch', fetchMock);
    const res = await checkServer('');
    expect(res.ok).toBe(false);
    expect(fetchMock).not.toHaveBeenCalled();
  });
});
