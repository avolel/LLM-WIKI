// Navigation wiring placeholder. A navigator (e.g. React Navigation) is added in Phase 8;
// for Phase 0 the app renders the HomeScreen directly to prove API connectivity.
export const ROUTES = {
  Home: 'Home',
  Chat: 'Chat',
  Browse: 'Browse',
  Projects: 'Projects',
} as const;

export type RouteName = (typeof ROUTES)[keyof typeof ROUTES];
