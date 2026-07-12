// Design tokens (expanded in Phase 8). Existing keys are kept so HomeScreen/CheckRow are unaffected.
export const theme = {
  colors: {
    background: '#ffffff',
    surface: '#f6f8fa',
    text: '#11181C',
    muted: '#687076',
    pass: '#1a7f37',
    fail: '#cf222e',
    accent: '#0a7ea4',
    border: '#e1e4e8',
    userBubble: '#0a7ea4',
    userText: '#ffffff',
    code: '#f0f1f3',
  },
  radius: { sm: 6, md: 10, lg: 16 },
  font: { sm: 13, md: 15, lg: 18, xl: 28 },
  spacing: (n: number) => n * 8,
} as const;

export type Theme = typeof theme;
