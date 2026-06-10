import { StyleSheet, Text, View } from 'react-native';
import { theme } from '../theme';
import type { DiagnosticCheck } from '../api/client';

/** Renders a single diagnostics check as a pass/fail row. */
export function CheckRow({ check }: { check: DiagnosticCheck }) {
  return (
    <View style={styles.row}>
      <Text style={[styles.badge, { color: check.passed ? theme.colors.pass : theme.colors.fail }]}>
        {check.passed ? '✓' : '✗'}
      </Text>
      <View style={styles.body}>
        <Text style={styles.name}>{check.name}</Text>
        <Text style={styles.detail}>{check.detail}</Text>
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  row: {
    flexDirection: 'row',
    alignItems: 'flex-start',
    paddingVertical: theme.spacing(1),
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderBottomColor: theme.colors.border,
  },
  badge: { fontSize: 18, width: 28, fontWeight: '700' },
  body: { flex: 1 },
  name: { fontSize: 16, fontWeight: '600', color: theme.colors.text },
  detail: { fontSize: 13, color: theme.colors.muted },
});
