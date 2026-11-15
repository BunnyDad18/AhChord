/**
 * MT-02: shell boot-gate flow — first-run setup, boot health probe,
 * server-unreachable screen, change server. The web build must render
 * straight through with zero probing.
 */
import { createRoot } from 'react-dom/client';
import { act } from 'react-dom/test-utils';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ServerGate } from './ServerGate';

const setServerPlugin = vi.fn(() => Promise.resolve());

function asNative() {
  window.Capacitor = {
    isNativePlatform: () => true,
    Plugins: { AhChord: { setServer: setServerPlugin } },
  };
}
function asWeb() {
  delete window.Capacitor;
}

let container: HTMLDivElement | null = null;
let root: ReturnType<typeof createRoot> | null = null;

async function mount() {
  (globalThis as Record<string, unknown>).IS_REACT_ACT_ENVIRONMENT = true;
  container = document.createElement('div');
  document.body.appendChild(container);
  root = createRoot(container);
  await act(async () => {
    root!.render(
      <ServerGate>
        <div data-testid="app">APP-RENDERED</div>
      </ServerGate>,
    );
  });
}

function text(): string {
  return container?.textContent ?? '';
}

function button(label: string): HTMLButtonElement {
  const btn = [...(container?.querySelectorAll('button') ?? [])].find((b) =>
    (b.textContent ?? '').includes(label),
  ) as HTMLButtonElement;
  return btn;
}

function setInput(value: string) {
  const input = container!.querySelector('input') as HTMLInputElement;
  const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')!.set!;
  setter.call(input, value);
  input.dispatchEvent(new Event('input', { bubbles: true }));
}

async function submitForm() {
  const form = container!.querySelector('form') as HTMLFormElement;
  await act(async () => {
    form.dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
  });
}

beforeEach(() => {
  localStorage.clear();
  setServerPlugin.mockClear();
  asWeb();
});

afterEach(async () => {
  await act(async () => root?.unmount());
  container?.remove();
  container = null;
  vi.unstubAllGlobals();
});

describe('ServerGate', () => {
  it('web build renders straight through — no probe, no setup', async () => {
    const fetchMock = vi.fn(async (_url: string) => ({ ok: true, status: 200 }));
    vi.stubGlobal('fetch', fetchMock);
    await mount();
    expect(text()).toContain('APP-RENDERED');
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('shell first run: setup screen; a healthy address connects and persists', async () => {
    asNative();
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve({ ok: true, status: 200 })));
    await mount();

    expect(text()).toContain('Point the app at your AhChord server.');

    setInput('192.168.1.189:5000');
    await submitForm();

    expect(text()).toContain('APP-RENDERED');
    expect(localStorage.getItem('ahchord.server')).toBe('http://192.168.1.189:5000');
    expect(setServerPlugin).toHaveBeenCalledWith({ url: 'http://192.168.1.189:5000' });
  });

  it('shell boot with a saved healthy address: probes, reports to native, renders', async () => {
    asNative();
    localStorage.setItem('ahchord.server', 'http://10.0.0.9:5000');
    const fetchMock = vi.fn(async (_url: string) => ({ ok: true, status: 200 }));
    vi.stubGlobal('fetch', fetchMock);
    await mount();

    expect(fetchMock.mock.calls[0][0]).toBe('http://10.0.0.9:5000/api/health');
    expect(text()).toContain('APP-RENDERED');
    expect(setServerPlugin).toHaveBeenCalledWith({ url: 'http://10.0.0.9:5000' });
  });

  it('shell boot with an unreachable server: no white screen — retry / change server', async () => {
    asNative();
    localStorage.setItem('ahchord.server', 'http://10.255.255.1:5000');
    vi.stubGlobal('fetch', vi.fn(() => Promise.reject(new TypeError('Failed to fetch'))));
    await mount();

    expect(text()).toContain('Server unreachable');
    expect(text()).toContain('http://10.255.255.1:5000');

    await act(async () => button('Change server').dispatchEvent(new MouseEvent('click', { bubbles: true })));
    expect(text()).toContain('Point the app at your AhChord server.');
    expect(localStorage.getItem('ahchord.server')).toBeNull();
  });
});
