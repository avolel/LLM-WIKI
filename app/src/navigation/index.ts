// Tab set for the Phase 8 custom TabBar (no navigation library — a useState switcher in App.tsx).
export const ROUTES = {
  Chat: 'Chat',
  Browse: 'Browse',
  Projects: 'Projects',
  Status: 'Status',
} as const;

export type RouteName = keyof typeof ROUTES;

export const TABS: { key: RouteName; label: string }[] = [
  { key: 'Chat', label: 'Chat' },
  { key: 'Browse', label: 'Browse' },
  { key: 'Projects', label: 'Projects' },
  { key: 'Status', label: 'Status' },
];
