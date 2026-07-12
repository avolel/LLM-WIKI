import { useCallback, useEffect, useState } from 'react';
import {
  ActivityIndicator,
  Pressable,
  RefreshControl,
  ScrollView,
  StyleSheet,
  Text,
  View,
} from 'react-native';
import { getWikiTree, type WikiTree } from '../api/client';
import { useApp } from '../state';
import { theme } from '../theme';
import { PageModal } from '../components/PageModal';

type State =
  | { kind: 'idle' }
  | { kind: 'loading' }
  | { kind: 'error'; message: string }
  | { kind: 'ready'; tree: WikiTree };

/** Wiki-structure browser (BR-073): pages grouped by category, tap to open, pull to refresh. */
export function BrowseScreen() {
  const { activeProject } = useApp();
  const [state, setState] = useState<State>({ kind: 'idle' });
  const [openPath, setOpenPath] = useState<string | null>(null);

  const load = useCallback(async () => {
    if (!activeProject) {
      setState({ kind: 'idle' });
      return;
    }
    setState({ kind: 'loading' });
    try {
      const tree = await getWikiTree(activeProject);
      setState({ kind: 'ready', tree });
    } catch (err) {
      setState({ kind: 'error', message: err instanceof Error ? err.message : String(err) });
    }
  }, [activeProject]);

  useEffect(() => {
    void load();
  }, [load]);

  if (!activeProject) {
    return (
      <View style={styles.center}>
        <Text style={styles.muted}>No active project — select one on the Projects tab.</Text>
      </View>
    );
  }

  const isEmpty = state.kind === 'ready' && state.tree.categories.length === 0;

  return (
    <View style={styles.container}>
      <ScrollView
        contentContainerStyle={styles.body}
        refreshControl={
          <RefreshControl refreshing={state.kind === 'loading'} onRefresh={load} />
        }
      >
        <Text style={styles.header}>{activeProject}</Text>

        {state.kind === 'loading' && (
          <View style={styles.center}>
            <ActivityIndicator color={theme.colors.accent} />
          </View>
        )}
        {state.kind === 'error' && <Text style={styles.error}>{state.message}</Text>}
        {isEmpty && <Text style={styles.muted}>No pages yet — ingest a source with the CLI.</Text>}

        {state.kind === 'ready' &&
          state.tree.categories.map((cat) => (
            <View key={cat.name} style={styles.section}>
              <Text style={styles.category}>{cat.name}</Text>
              {cat.pages.map((p) => (
                <Pressable
                  key={p.relativePath}
                  style={styles.pageRow}
                  onPress={() => setOpenPath(p.relativePath)}
                >
                  <Text style={styles.pageSlug}>{p.slug}</Text>
                </Pressable>
              ))}
            </View>
          ))}
      </ScrollView>

      {openPath && (
        <PageModal
          wiki={activeProject}
          relativePath={openPath}
          onClose={() => setOpenPath(null)}
        />
      )}
    </View>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1 },
  body: { padding: theme.spacing(2), gap: theme.spacing(2) },
  header: { fontSize: theme.font.xl, fontWeight: '800', color: theme.colors.text },
  section: { gap: theme.spacing(0.5) },
  category: {
    fontSize: theme.font.sm,
    fontWeight: '700',
    color: theme.colors.muted,
    textTransform: 'uppercase',
    marginTop: theme.spacing(1),
  },
  pageRow: {
    paddingVertical: theme.spacing(1),
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderBottomColor: theme.colors.border,
  },
  pageSlug: { fontSize: theme.font.md, color: theme.colors.text },
  center: { alignItems: 'center', justifyContent: 'center', flex: 1, padding: theme.spacing(4) },
  muted: { color: theme.colors.muted, textAlign: 'center' },
  error: { color: theme.colors.fail },
});
