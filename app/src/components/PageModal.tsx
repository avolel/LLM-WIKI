import { useEffect, useState } from 'react';
import {
  ActivityIndicator,
  Modal,
  Pressable,
  ScrollView,
  StyleSheet,
  Text,
  View,
} from 'react-native';
import { getPage, type WikiPage } from '../api/client';
import { theme } from '../theme';
import { Markdown } from './Markdown';

interface Props {
  wiki: string;
  relativePath: string;
  onClose: () => void;
}

type State =
  | { kind: 'loading' }
  | { kind: 'error'; message: string }
  | { kind: 'ready'; page: WikiPage };

/** A modal that fetches and renders one wiki page — opened from a citation (BR-071) or Browse (BR-073). */
export function PageModal({ wiki, relativePath, onClose }: Props) {
  const [state, setState] = useState<State>({ kind: 'loading' });

  useEffect(() => {
    let cancelled = false;
    setState({ kind: 'loading' });
    getPage(wiki, relativePath)
      .then((page) => !cancelled && setState({ kind: 'ready', page }))
      .catch((err) =>
        !cancelled &&
        setState({ kind: 'error', message: err instanceof Error ? err.message : String(err) }),
      );
    return () => {
      cancelled = true;
    };
  }, [wiki, relativePath]);

  return (
    <Modal visible animationType="slide" transparent onRequestClose={onClose}>
      <View style={styles.backdrop}>
        <View style={styles.sheet}>
          <View style={styles.header}>
            <Text style={styles.path} numberOfLines={1}>
              {relativePath}
            </Text>
            <Pressable onPress={onClose} accessibilityRole="button" style={styles.closeBtn}>
              <Text style={styles.closeText}>✕</Text>
            </Pressable>
          </View>

          {state.kind === 'loading' && (
            <View style={styles.center}>
              <ActivityIndicator color={theme.colors.accent} />
            </View>
          )}

          {state.kind === 'error' && (
            <View style={styles.center}>
              <Text style={styles.error}>Could not open page</Text>
              <Text style={styles.muted}>{state.message}</Text>
            </View>
          )}

          {state.kind === 'ready' && (
            <ScrollView contentContainerStyle={styles.body}>
              <Text style={styles.title}>{state.page.title}</Text>
              <View style={styles.metaRow}>
                <Text style={styles.badge}>{state.page.type}</Text>
                {state.page.tags.map((t) => (
                  <Text key={t} style={styles.tag}>
                    #{t}
                  </Text>
                ))}
              </View>
              <Markdown content={state.page.content} />
            </ScrollView>
          )}
        </View>
      </View>
    </Modal>
  );
}

const styles = StyleSheet.create({
  backdrop: { flex: 1, backgroundColor: 'rgba(0,0,0,0.35)', justifyContent: 'flex-end' },
  sheet: {
    maxHeight: '90%',
    backgroundColor: theme.colors.background,
    borderTopLeftRadius: theme.radius.lg,
    borderTopRightRadius: theme.radius.lg,
    paddingBottom: theme.spacing(2),
  },
  header: {
    flexDirection: 'row',
    alignItems: 'center',
    padding: theme.spacing(2),
    borderBottomWidth: StyleSheet.hairlineWidth,
    borderBottomColor: theme.colors.border,
  },
  path: { flex: 1, fontSize: theme.font.sm, color: theme.colors.muted },
  closeBtn: { padding: theme.spacing(0.5) },
  closeText: { fontSize: theme.font.lg, color: theme.colors.muted },
  center: { alignItems: 'center', gap: theme.spacing(1), padding: theme.spacing(4) },
  body: { padding: theme.spacing(2), gap: theme.spacing(1.5) },
  title: { fontSize: 24, fontWeight: '800', color: theme.colors.text },
  metaRow: { flexDirection: 'row', flexWrap: 'wrap', gap: theme.spacing(1), alignItems: 'center' },
  badge: {
    fontSize: 11,
    color: theme.colors.accent,
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: theme.colors.accent,
    borderRadius: theme.radius.sm,
    paddingHorizontal: theme.spacing(0.75),
    paddingVertical: 2,
    textTransform: 'uppercase',
  },
  tag: { fontSize: theme.font.sm, color: theme.colors.muted },
  error: { fontSize: theme.font.md, color: theme.colors.fail, fontWeight: '700' },
  muted: { color: theme.colors.muted, textAlign: 'center' },
});
