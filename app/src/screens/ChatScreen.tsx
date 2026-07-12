import { useRef, useState } from 'react';
import {
  ActivityIndicator,
  Pressable,
  ScrollView,
  StyleSheet,
  Text,
  TextInput,
  View,
} from 'react-native';
import {
  postQuery,
  saveAnswer,
  type ConversationTurn,
  type QueryResult,
} from '../api/client';
import { useApp } from '../state';
import { theme } from '../theme';
import { Markdown } from '../components/Markdown';
import { CitationChip } from '../components/CitationChip';
import { PageModal } from '../components/PageModal';

interface Message {
  role: 'user' | 'agent';
  text: string;
  result?: QueryResult;
  saved?: string; // title once saved
}

/**
 * Chat surface (BR-070/071/072/074): ask grounded questions, see markdown answers with clickable
 * citations, carry follow-up history (BR-044), save covered answers (BR-045), and see honest gaps.
 */
export function ChatScreen() {
  const { activeProject } = useApp();
  const [messages, setMessages] = useState<Message[]>([]);
  const [history, setHistory] = useState<ConversationTurn[]>([]);
  const [input, setInput] = useState('');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [openPath, setOpenPath] = useState<string | null>(null);
  const scrollRef = useRef<ScrollView>(null);

  const send = async () => {
    const q = input.trim();
    if (!q || loading) return;
    if (!activeProject) {
      setError('Pick a project on the Projects tab first.');
      return;
    }
    setError(null);
    setInput('');
    setMessages((m) => [...m, { role: 'user', text: q }]);
    setLoading(true);
    try {
      const result = await postQuery(activeProject, q, history);
      setMessages((m) => [...m, { role: 'agent', text: result.answer, result }]);
      setHistory((h) => [...h, { question: q, answer: result.answer }]);
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      setLoading(false);
      requestAnimationFrame(() => scrollRef.current?.scrollToEnd({ animated: true }));
    }
  };

  const onSave = async (index: number, result: QueryResult) => {
    if (!activeProject) return;
    try {
      const outcome = await saveAnswer(activeProject, result);
      setMessages((m) =>
        m.map((msg, i) => (i === index ? { ...msg, saved: outcome.title } : msg)),
      );
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    }
  };

  return (
    <View style={styles.container}>
      <ScrollView ref={scrollRef} contentContainerStyle={styles.thread}>
        {!activeProject && (
          <Text style={styles.hint}>No active project — select one on the Projects tab.</Text>
        )}
        {messages.length === 0 && activeProject && (
          <Text style={styles.hint}>Ask a question about “{activeProject}”.</Text>
        )}

        {messages.map((msg, i) =>
          msg.role === 'user' ? (
            <View key={i} style={styles.userRow}>
              <View style={styles.userBubble}>
                <Text style={styles.userText}>{msg.text}</Text>
              </View>
            </View>
          ) : (
            <View key={i} style={styles.agentRow}>
              <Markdown content={msg.text} onLinkPress={setOpenPath} />
              {msg.result && !msg.result.covered && (
                <Text style={styles.gap}>Not covered by this wiki.</Text>
              )}
              {msg.result && msg.result.covered && msg.result.citations.length > 0 && (
                <View style={styles.chips}>
                  {msg.result.citations.map((c) => (
                    <CitationChip key={c.relativePath} citation={c} onOpen={setOpenPath} />
                  ))}
                </View>
              )}
              {msg.result &&
                msg.result.covered &&
                (msg.saved ? (
                  <Text style={styles.saved}>Saved as “{msg.saved}”.</Text>
                ) : (
                  <Pressable style={styles.saveBtn} onPress={() => onSave(i, msg.result!)}>
                    <Text style={styles.saveText}>Save answer</Text>
                  </Pressable>
                ))}
            </View>
          ),
        )}

        {loading && (
          <View style={styles.thinking}>
            <ActivityIndicator color={theme.colors.accent} />
            <Text style={styles.muted}>Thinking…</Text>
          </View>
        )}
        {error && <Text style={styles.error}>{error}</Text>}
      </ScrollView>

      <View style={styles.inputBar}>
        <TextInput
          style={styles.input}
          value={input}
          onChangeText={setInput}
          placeholder="Ask a question…"
          placeholderTextColor={theme.colors.muted}
          editable={!loading}
          onSubmitEditing={send}
          returnKeyType="send"
        />
        <Pressable
          style={[styles.sendBtn, (loading || !input.trim()) && styles.sendBtnDisabled]}
          onPress={send}
          disabled={loading || !input.trim()}
        >
          <Text style={styles.sendText}>Send</Text>
        </Pressable>
      </View>

      {openPath && activeProject && (
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
  thread: { padding: theme.spacing(2), gap: theme.spacing(2) },
  hint: { color: theme.colors.muted, textAlign: 'center', marginTop: theme.spacing(2) },
  userRow: { alignItems: 'flex-end' },
  userBubble: {
    backgroundColor: theme.colors.userBubble,
    borderRadius: theme.radius.md,
    paddingVertical: theme.spacing(1),
    paddingHorizontal: theme.spacing(1.5),
    maxWidth: '85%',
  },
  userText: { color: theme.colors.userText, fontSize: theme.font.md },
  agentRow: {
    backgroundColor: theme.colors.surface,
    borderRadius: theme.radius.md,
    padding: theme.spacing(1.5),
    gap: theme.spacing(1),
  },
  gap: { color: theme.colors.fail, fontStyle: 'italic', fontSize: theme.font.sm },
  chips: { flexDirection: 'row', flexWrap: 'wrap', gap: theme.spacing(1) },
  saveBtn: {
    alignSelf: 'flex-start',
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: theme.colors.accent,
    borderRadius: theme.radius.sm,
    paddingVertical: theme.spacing(0.5),
    paddingHorizontal: theme.spacing(1),
  },
  saveText: { color: theme.colors.accent, fontSize: theme.font.sm, fontWeight: '600' },
  saved: { color: theme.colors.pass, fontSize: theme.font.sm },
  thinking: { flexDirection: 'row', alignItems: 'center', gap: theme.spacing(1) },
  muted: { color: theme.colors.muted },
  error: { color: theme.colors.fail, fontSize: theme.font.sm },
  inputBar: {
    flexDirection: 'row',
    gap: theme.spacing(1),
    padding: theme.spacing(1.5),
    borderTopWidth: StyleSheet.hairlineWidth,
    borderTopColor: theme.colors.border,
  },
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
  sendBtn: {
    justifyContent: 'center',
    backgroundColor: theme.colors.accent,
    borderRadius: theme.radius.md,
    paddingHorizontal: theme.spacing(2),
  },
  sendBtnDisabled: { opacity: 0.5 },
  sendText: { color: '#fff', fontWeight: '700' },
});
