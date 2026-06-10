import { ActivityIndicator, Button, ScrollView, StyleSheet, Text, View } from 'react-native';
import { API_BASE_URL } from '../api/client';
import { CheckRow } from '../components/CheckRow';
import { useDiagnostics } from '../hooks/useDiagnostics';
import { theme } from '../theme';

/**
 * Phase 0 home screen: proves the Expo client reaches the API and shows the backend
 * connectivity diagnostics (Oracle, embedding, chat). Full UI arrives in Phase 8.
 */
export function HomeScreen() {
  const { state, refresh } = useDiagnostics();

  return (
    <ScrollView contentContainerStyle={styles.container}>
      <Text style={styles.title}>LLM Wiki</Text>
      <Text style={styles.subtitle}>API: {API_BASE_URL}</Text>

      {state.kind === 'loading' && (
        <View style={styles.center}>
          <ActivityIndicator color={theme.colors.accent} />
          <Text style={styles.muted}>Checking connectivity…</Text>
        </View>
      )}

      {state.kind === 'unreachable' && (
        <View style={styles.center}>
          <Text style={[styles.status, { color: theme.colors.fail }]}>API unreachable</Text>
          <Text style={styles.muted}>{state.error}</Text>
        </View>
      )}

      {state.kind === 'ready' && (
        <View style={styles.section}>
          <Text
            style={[
              styles.status,
              { color: state.report.allPassed ? theme.colors.pass : theme.colors.fail },
            ]}
          >
            {state.report.allPassed ? 'All systems go' : 'Some checks failed'}
          </Text>
          {state.report.checks.map((check) => (
            <CheckRow key={check.name} check={check} />
          ))}
        </View>
      )}

      <View style={styles.section}>
        <Button title="Re-run diagnostics" color={theme.colors.accent} onPress={refresh} />
      </View>
    </ScrollView>
  );
}

const styles = StyleSheet.create({
  container: { padding: theme.spacing(3), gap: theme.spacing(2) },
  title: { fontSize: 28, fontWeight: '800', color: theme.colors.text },
  subtitle: { fontSize: 13, color: theme.colors.muted },
  section: { marginTop: theme.spacing(2), gap: theme.spacing(1) },
  center: { alignItems: 'center', gap: theme.spacing(1), marginTop: theme.spacing(4) },
  status: { fontSize: 18, fontWeight: '700' },
  muted: { color: theme.colors.muted, textAlign: 'center' },
});
