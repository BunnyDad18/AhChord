import { create } from 'zustand';

export type ToastKind = 'success' | 'error' | 'info';

export interface ToastItem {
  id: number;
  kind: ToastKind;
  message: string;
}

interface ToastState {
  toasts: ToastItem[];
  push: (message: string, kind?: ToastKind) => void;
  dismiss: (id: number) => void;
}

let nextId = 1;

/**
 * Toast queue for mutation feedback (ST-22/23/24). Push from anywhere via
 * `useToastStore.getState().push(...)`; rendered once by <ToastStack/>.
 * Auto-dismisses after 4s; keeps at most 4 visible.
 */
export const useToastStore = create<ToastState>()((set) => ({
  toasts: [],
  push: (message, kind: ToastKind = 'success') => {
    const id = nextId++;
    set((s) => ({ toasts: [...s.toasts.slice(-3), { id, kind, message }] }));
    setTimeout(() => {
      set((s) => ({ toasts: s.toasts.filter((t) => t.id !== id) }));
    }, 4000);
  },
  dismiss: (id) => set((s) => ({ toasts: s.toasts.filter((t) => t.id !== id) })),
}));
