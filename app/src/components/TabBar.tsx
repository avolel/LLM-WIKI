import { Pressable, StyleSheet, Text, View } from 'react-native';
import { theme } from '../theme';
import type { RouteName } from '../navigation';

interface Props {
  tabs: { key: RouteName; label: string }[];
  active: RouteName;
  onSelect: (key: RouteName) => void;
}

/** Bottom tab bar: a Pressable per tab, highlighting the active one. No navigation library. */
export function TabBar({ tabs, active, onSelect }: Props) {
  return (
    <View style={styles.bar}>
      {tabs.map((t) => {
        const isActive = t.key === active;
        return (
          <Pressable
            key={t.key}
            style={styles.tab}
            onPress={() => onSelect(t.key)}
            accessibilityRole="tab"
            accessibilityState={{ selected: isActive }}
          >
            <Text style={[styles.label, isActive && styles.labelActive]}>{t.label}</Text>
            {isActive && <View style={styles.indicator} />}
          </Pressable>
        );
      })}
    </View>
  );
}

const styles = StyleSheet.create({
  bar: {
    flexDirection: 'row',
    borderTopWidth: StyleSheet.hairlineWidth,
    borderTopColor: theme.colors.border,
    backgroundColor: theme.colors.surface,
  },
  tab: { flex: 1, alignItems: 'center', paddingVertical: theme.spacing(1.25), gap: 4 },
  label: { fontSize: theme.font.sm, color: theme.colors.muted, fontWeight: '600' },
  labelActive: { color: theme.colors.accent },
  indicator: { height: 2, width: 24, borderRadius: 2, backgroundColor: theme.colors.accent },
});
