import { StyleSheet, Text, View } from 'react-native';
import { theme } from '../theme';

/** Placeholder for the Chat experience (built in Phase 8). */
export function ChatScreen() {
  return (
    <View style={styles.container}>
      <Text style={styles.title}>Chat</Text>
      <Text style={styles.muted}>Coming in a later phase.</Text>
    </View>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1, alignItems: 'center', justifyContent: 'center', gap: theme.spacing(1) },
  title: { fontSize: 22, fontWeight: '700', color: theme.colors.text },
  muted: { color: theme.colors.muted },
});
