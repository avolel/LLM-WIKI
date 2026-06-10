// Minimal design tokens. Expanded into a full theme in Phase 8.
export const theme = {
  colors: {
    background: '#ffffff',
    text: '#11181C',
    muted: '#687076',
    pass: '#1a7f37',
    fail: '#cf222e',
    accent: '#0a7ea4',
    border: '#e1e4e8',
  },
  spacing: (n: number) => n * 8,
} as const;

export type Theme = typeof theme;
