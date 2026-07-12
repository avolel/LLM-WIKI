import { useCallback, useEffect, useState } from 'react';
import {
  ActivityIndicator,
  Pressable,
  ScrollView,
  StyleSheet,
  Text,
  TextInput,
  View,
} from 'react-native';
import { createProject, deleteProject, listProjects, type ProjectInfo } from '../api/client';
import { useApp } from '../state';
import { theme } from '../theme';

type State =
  | { kind: 'loading' }
  | { kind: 'error'; message: string }
  | { kind: 'ready'; projects: ProjectInfo[] };

/** Projects list/select/create (BR-050/052): pick the active project, or create a new one. */
export function ProjectsScreen() {
  const { activeProject, setActiveProject } = useApp();
  const [state, setState] = useState<State>({ kind: 'loading' });
  const [name, setName] = useState('');
  const [creating, setCreating] = useState(false);
  const [createError, setCreateError] = useState<string | null>(null);
  const [deleting, setDeleting] = useState<string | null>(null);

  const load = useCallback(async () => {
    setState({ kind: 'loading' });
    try {
      const projects = await listProjects();
      setState({ kind: 'ready', projects });
    } catch (err) {
      setState({ kind: 'error', message: err instanceof Error ? err.message : String(err) });
    }
  }, []);

  const remove = async (name: string) => {
  if (deleting) return;
  if (typeof window !== 'undefined' &&
      !window.confirm(`Delete "${name}" and all its data? This cannot be undone.`)) return;
  setDeleting(name);
  setCreateError(null);
  try {
    await deleteProject(name);
    if (name === activeProject) setActiveProject(null);   // clears localStorage (existing behaviour)
    await load();
  } catch (err) {
    setCreateError(err instanceof Error ? err.message : String(err));
  } finally {
    setDeleting(null);
  }
};

  useEffect(() => {
    void load();
  }, [load]);

  const create = async () => {
    const n = name.trim();
    if (!n || creating) return;
    setCreating(true);
    setCreateError(null);
    try {
      const created = await createProject(n);
      setName('');
      setActiveProject(created.name);
      await load();
    } catch (err) {
      setCreateError(err instanceof Error ? err.message : String(err));
    } finally {
      setCreating(false);
    }
  };

  const fmt = (iso: string | null) => (iso ? new Date(iso).toLocaleDateString() : 'never');

  return (
    <ScrollView contentContainerStyle={styles.body}>
      <Text style={styles.header}>Projects</Text>

      {state.kind === 'loading' && (
        <View style={styles.center}>
          <ActivityIndicator color={theme.colors.accent} />
        </View>
      )}
      {state.kind === 'error' && <Text style={styles.error}>{state.message}</Text>}

      {state.kind === 'ready' && state.projects.length === 0 && (
        <Text style={styles.muted}>No projects yet — create one below.</Text>
      )}

      {state.kind === 'ready' &&
        state.projects.map((p) => {
          const active = p.name === activeProject;
          return (
            <Pressable
              key={p.name}
              style={[styles.card, active && styles.cardActive]}
              onPress={() => setActiveProject(p.name)}
            >
              <View style={styles.cardHeader}>
                <Text style={styles.cardName}>
                  {active ? '★ ' : ''}
                  {p.name}
                </Text>
                <Pressable
                  style={styles.deleteBtn}
                  disabled={deleting === p.name}
                  onPress={(e) => { e.stopPropagation?.(); void remove(p.name); }}
                >
                  <Text style={styles.deleteText}>{deleting === p.name ? '…' : 'Delete'}</Text>
                </Pressable>
              </View>
              <Text style={styles.meta}>
                {p.pageCount} pages · {p.sourceCount} sources · last ingest {fmt(p.lastIngestAt)}
              </Text>
            </Pressable>
          );
        })}

      <View style={styles.createRow}>
        <TextInput
          style={styles.input}
          value={name}
          onChangeText={setName}
          placeholder="New project name…"
          placeholderTextColor={theme.colors.muted}
          editable={!creating}
          onSubmitEditing={create}
          autoCapitalize="none"
        />
        <Pressable
          style={[styles.createBtn, (creating || !name.trim()) && styles.createBtnDisabled]}
          onPress={create}
          disabled={creating || !name.trim()}
        >
          <Text style={styles.createText}>{creating ? '…' : 'Create'}</Text>
        </Pressable>
      </View>
      {createError && <Text style={styles.error}>{createError}</Text>}
    </ScrollView>
  );
}

const styles = StyleSheet.create({
  body: { padding: theme.spacing(2), gap: theme.spacing(1.5) },
  header: { fontSize: theme.font.xl, fontWeight: '800', color: theme.colors.text },
  center: { alignItems: 'center', padding: theme.spacing(4) },
  card: {
    backgroundColor: theme.colors.surface,
    borderRadius: theme.radius.md,
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: theme.colors.border,
    padding: theme.spacing(1.5),
    gap: theme.spacing(0.5),
  },
  cardActive: { borderColor: theme.colors.accent },
  cardHeader: { flexDirection: 'row', justifyContent: 'space-between' },
  cardName: { fontSize: theme.font.md, fontWeight: '700', color: theme.colors.text },
  meta: { fontSize: theme.font.sm, color: theme.colors.muted },
  createRow: { flexDirection: 'row', gap: theme.spacing(1), marginTop: theme.spacing(1) },
  input: {
    flex: 1,
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: theme.colors.border,
    borderRadius: theme.radius.md,
    paddingVertical: theme.spacing(1),
    paddingHorizontal: theme.spacing(1.5),
    color: theme.colors.text,
    fontSize: theme.font.md,
  },
  createBtn: {
    justifyContent: 'center',
    backgroundColor: theme.colors.accent,
    borderRadius: theme.radius.md,
    paddingHorizontal: theme.spacing(2),
  },
  createBtnDisabled: { opacity: 0.5 },
  createText: { color: '#fff', fontWeight: '700' },
  muted: { color: theme.colors.muted },
  error: { color: theme.colors.fail, fontSize: theme.font.sm },
  deleteBtn: {
    justifyContent: 'center',
    backgroundColor: theme.colors.fail,
    borderRadius: theme.radius.md,
    paddingHorizontal: theme.spacing(2),
  },
  deleteText: { color: '#fff', fontWeight: '700' },
});
