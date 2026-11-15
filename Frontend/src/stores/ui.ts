import { create } from 'zustand';
import { persist } from 'zustand/middleware';
import { DEFAULT_LOGO, type LogoId } from '../components/Logos';

export type Theme = 'dark' | 'light';
export type FontSize = 'S' | 'M' | 'L';
export type DiagramType = 'guitar' | 'piano';

/** Reflect the active theme on <html> so the CSS tokens apply. */
export function applyTheme(theme: Theme): void {
  document.documentElement.setAttribute('data-theme', theme);
}

/** Reflect the active font-size preference on <html>. */
export function applyFontSize(size: FontSize): void {
  document.documentElement.setAttribute('data-font-size', size);
}

interface UiState {
  theme: Theme;
  fontSize: FontSize;
  diagramType: DiagramType;
  logo: LogoId;
  /** ST-70d: phone "More" sheet — transient, never persisted. */
  mobileMenuOpen: boolean;
  setTheme: (theme: Theme) => void;
  toggleTheme: () => void;
  setFontSize: (size: FontSize) => void;
  setDiagramType: (type: DiagramType) => void;
  setLogo: (logo: LogoId) => void;
  setMobileMenuOpen: (open: boolean) => void;
}

/**
 * UI preferences store. Dark is the default (design doc). All values persist to
 * localStorage. `diagramType` (guitar/piano) is consumed by the chord-diagram
 * tooltips in ST-20/ST-21. `logo` (ST-66) picks the brand mark in the top bar.
 */
export const useUiStore = create<UiState>()(
  persist(
    (set, get) => ({
      theme: 'dark',
      fontSize: 'M',
      diagramType: 'guitar',
      logo: DEFAULT_LOGO,
      mobileMenuOpen: false,
      setTheme: (theme) => {
        applyTheme(theme);
        set({ theme });
      },
      toggleTheme: () => {
        const next: Theme = get().theme === 'dark' ? 'light' : 'dark';
        applyTheme(next);
        set({ theme: next });
      },
      setFontSize: (fontSize) => {
        applyFontSize(fontSize);
        set({ fontSize });
      },
      setDiagramType: (diagramType) => set({ diagramType }),
      setLogo: (logo) => set({ logo }),
      setMobileMenuOpen: (mobileMenuOpen) => set({ mobileMenuOpen }),
    }),
    {
      name: 'ahchord-ui',
      // ST-70d: the More sheet is transient UI state — keep it out of localStorage.
      // (Cast: zustand merges the persisted subset into the initial state on rehydrate.)
      partialize: (s) =>
        ({ theme: s.theme, fontSize: s.fontSize, diagramType: s.diagramType, logo: s.logo }) as UiState,
    },
  ),
);
