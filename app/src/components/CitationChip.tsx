import { Pressable, StyleSheet, Text } from 'react-native';
import { theme } from '../theme';
import type { Citation } from '../api/client';

interface Props {
  citation: Citation;
  onOpen: (relativePath: string) => void;
}

/** A tappable pill for one citation — title + type. Opens the cited page (BR-071). */
export function CitationChip({ citation, onOpen }: Props) {
  return (
    <Pressable
      style={styles.chip}
      onPress={() => onOpen(citation.relativePath)}
      accessibilityRole="link"
    >
      <Text style={styles.title} numberOfLines={1}>
        {citation.title}
      </Text>
      <Text style={styles.type}>{citation.type}</Text>
    </Pressable>
  );
}

const styles = StyleSheet.create({
  chip: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: theme.spacing(0.5),
    backgroundColor: theme.colors.surface,
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: theme.colors.accent,
    borderRadius: theme.radius.sm,
    paddingVertical: theme.spacing(0.5),
    paddingHorizontal: theme.spacing(1),
  },
  title: { fontSize: theme.font.sm, color: theme.colors.accent, fontWeight: '600', maxWidth: 200 },
  type: { fontSize: 11, color: theme.colors.muted, textTransform: 'uppercase' },
});
