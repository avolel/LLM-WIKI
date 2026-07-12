import React, { createContext, useContext, useMemo, useState } from 'react';

// App state (Phase 8): the active project (BR-050 "select the active project on startup"),
// persisted to localStorage on web when available; in-memory only on native.
const KEY = 'llmwiki.activeProject';

const load = (): string | null => {
  try {
    return typeof localStorage !== 'undefined' ? localStorage.getItem(KEY) : null;
  } catch {
    return null;
  }
};

const save = (v: string | null): void => {
  try {
    if (typeof localStorage !== 'undefined') {
      if (v) localStorage.setItem(KEY, v);
      else localStorage.removeItem(KEY);
    }
  } catch {
    /* non-web: in-memory only */
  }
};

interface AppState {
  activeProject: string | null;
  setActiveProject: (p: string | null) => void;
}

const AppContext = createContext<AppState | null>(null);

export function AppProvider({ children }: { children: React.ReactNode }) {
  const [activeProject, setActiveProjectRaw] = useState<string | null>(load);
  const value = useMemo<AppState>(
    () => ({
      activeProject,
      setActiveProject: (p) => {
        setActiveProjectRaw(p);
        save(p);
      },
    }),
    [activeProject],
  );
  return <AppContext.Provider value={value}>{children}</AppContext.Provider>;
}

export function useApp(): AppState {
  const ctx = useContext(AppContext);
  if (!ctx) throw new Error('useApp must be used within AppProvider');
  return ctx;
}
