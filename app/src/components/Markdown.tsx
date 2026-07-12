import React from 'react';
import { StyleSheet, Text, View } from 'react-native';
import { theme } from '../theme';

// A small, dependency-free markdown renderer for the subset the agent produces (BR-070/043,
// NFR-09): headings, paragraphs, bullet/numbered lists, fenced + inline code, bold/italic,
// [text](href) links, and simple pipe tables. Unknown syntax falls back to plain text.

interface Props {
  content: string;
  onLinkPress?: (href: string) => void;
}

export function Markdown({ content, onLinkPress }: Props) {
  return <View style={styles.doc}>{renderBlocks(content, onLinkPress)}</View>;
}

// ---- block parser (line-based) ----
function renderBlocks(md: string, onLinkPress?: (href: string) => void): React.ReactNode[] {
  const lines = md.replace(/\r\n/g, '\n').split('\n');
  const out: React.ReactNode[] = [];
  let i = 0;
  let key = 0;
  const k = () => `b${key++}`;

  while (i < lines.length) {
    const line = lines[i];

    // fenced code block
    if (line.trim().startsWith('```')) {
      const body: string[] = [];
      i++;
      while (i < lines.length && !lines[i].trim().startsWith('```')) {
        body.push(lines[i]);
        i++;
      }
      i++; // consume closing fence
      out.push(
        <View key={k()} style={styles.codeBlock}>
          <Text style={styles.codeText}>{body.join('\n')}</Text>
        </View>,
      );
      continue;
    }

    // blank line
    if (line.trim() === '') {
      i++;
      continue;
    }

    // heading
    const heading = /^(#{1,3})\s+(.*)$/.exec(line);
    if (heading) {
      const level = heading[1].length;
      const style = level === 1 ? styles.h1 : level === 2 ? styles.h2 : styles.h3;
      out.push(
        <Text key={k()} style={style}>
          {renderInline(heading[2], onLinkPress)}
        </Text>,
      );
      i++;
      continue;
    }

    // table: a header row of pipes followed by a separator row of dashes
    if (line.includes('|') && i + 1 < lines.length && /^[\s|:-]+$/.test(lines[i + 1]) && lines[i + 1].includes('-')) {
      const rows: string[] = [];
      const header = line;
      i += 2; // skip header + separator
      while (i < lines.length && lines[i].includes('|') && lines[i].trim() !== '') {
        rows.push(lines[i]);
        i++;
      }
      out.push(renderTable(k(), header, rows, onLinkPress));
      continue;
    }

    // list (bullet or numbered)
    if (/^\s*([-*]|\d+\.)\s+/.test(line)) {
      const items: { text: string; ordered: boolean; marker: string }[] = [];
      while (i < lines.length && /^\s*([-*]|\d+\.)\s+/.test(lines[i])) {
        const m = /^\s*([-*]|\d+\.)\s+(.*)$/.exec(lines[i])!;
        const ordered = /\d+\./.test(m[1]);
        items.push({ text: m[2], ordered, marker: m[1] });
        i++;
      }
      out.push(
        <View key={k()} style={styles.list}>
          {items.map((it, idx) => (
            <View key={idx} style={styles.listItem}>
              <Text style={styles.bullet}>{it.ordered ? it.marker : '•'}</Text>
              <Text style={styles.paragraph}>{renderInline(it.text, onLinkPress)}</Text>
            </View>
          ))}
        </View>,
      );
      continue;
    }

    // paragraph: gather consecutive non-blank, non-special lines
    const para: string[] = [line];
    i++;
    while (
      i < lines.length &&
      lines[i].trim() !== '' &&
      !lines[i].trim().startsWith('```') &&
      !/^(#{1,3})\s+/.test(lines[i]) &&
      !/^\s*([-*]|\d+\.)\s+/.test(lines[i])
    ) {
      para.push(lines[i]);
      i++;
    }
    out.push(
      <Text key={k()} style={styles.paragraph}>
        {renderInline(para.join(' '), onLinkPress)}
      </Text>,
    );
  }

  return out;
}

function renderTable(
  key: string,
  header: string,
  rows: string[],
  onLinkPress?: (href: string) => void,
): React.ReactNode {
  const cells = (row: string) =>
    row.replace(/^\s*\|/, '').replace(/\|\s*$/, '').split('|').map((c) => c.trim());
  const headers = cells(header);
  return (
    <View key={key} style={styles.table}>
      <View style={[styles.tableRow, styles.tableHeaderRow]}>
        {headers.map((c, idx) => (
          <Text key={idx} style={[styles.tableCell, styles.tableHeaderCell]}>
            {renderInline(c, onLinkPress)}
          </Text>
        ))}
      </View>
      {rows.map((r, ri) => {
        const cs = cells(r);
        return (
          <View key={ri} style={styles.tableRow}>
            {headers.map((_, ci) => (
              <Text key={ci} style={styles.tableCell}>
                {renderInline(cs[ci] ?? '', onLinkPress)}
              </Text>
            ))}
          </View>
        );
      })}
    </View>
  );
}

// ---- inline parser (bold / italic / code / links) ----
function renderInline(text: string, onLinkPress?: (href: string) => void): React.ReactNode[] {
  // Ordered so links are matched before emphasis; each alternative captures its own groups.
  const pattern =
    /(\[([^\]]+)\]\(([^)]+)\))|(`([^`]+)`)|(\*\*([^*]+)\*\*)|(\*([^*]+)\*)/g;
  const nodes: React.ReactNode[] = [];
  let last = 0;
  let m: RegExpExecArray | null;
  let key = 0;

  while ((m = pattern.exec(text)) !== null) {
    if (m.index > last) nodes.push(text.slice(last, m.index));
    if (m[1]) {
      // link: [label](href)
      const label = m[3];
      const href = m[4];
      nodes.push(
        <Text
          key={key++}
          style={styles.link}
          onPress={onLinkPress ? () => onLinkPress(href) : undefined}
        >
          {label}
        </Text>,
      );
    } else if (m[5]) {
      nodes.push(
        <Text key={key++} style={styles.inlineCode}>
          {m[6]}
        </Text>,
      );
    } else if (m[7]) {
      nodes.push(
        <Text key={key++} style={styles.bold}>
          {m[8]}
        </Text>,
      );
    } else if (m[9]) {
      nodes.push(
        <Text key={key++} style={styles.italic}>
          {m[10]}
        </Text>,
      );
    }
    last = pattern.lastIndex;
  }
  if (last < text.length) nodes.push(text.slice(last));
  return nodes;
}

const styles = StyleSheet.create({
  doc: { gap: theme.spacing(1) },
  h1: { fontSize: 22, fontWeight: '800', color: theme.colors.text, marginTop: theme.spacing(0.5) },
  h2: { fontSize: theme.font.lg, fontWeight: '700', color: theme.colors.text, marginTop: theme.spacing(0.5) },
  h3: { fontSize: theme.font.md, fontWeight: '700', color: theme.colors.text },
  paragraph: { fontSize: theme.font.md, color: theme.colors.text, lineHeight: 22, flexShrink: 1 },
  bold: { fontWeight: '700' },
  italic: { fontStyle: 'italic' },
  link: { color: theme.colors.accent, textDecorationLine: 'underline' },
  inlineCode: {
    fontFamily: 'monospace',
    backgroundColor: theme.colors.code,
    fontSize: theme.font.sm,
  },
  codeBlock: {
    backgroundColor: theme.colors.code,
    borderRadius: theme.radius.sm,
    padding: theme.spacing(1),
  },
  codeText: { fontFamily: 'monospace', fontSize: theme.font.sm, color: theme.colors.text },
  list: { gap: theme.spacing(0.5) },
  listItem: { flexDirection: 'row', gap: theme.spacing(1) },
  bullet: { fontSize: theme.font.md, color: theme.colors.muted, minWidth: 18 },
  table: {
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: theme.colors.border,
    borderRadius: theme.radius.sm,
    overflow: 'hidden',
  },
  tableRow: { flexDirection: 'row' },
  tableHeaderRow: { backgroundColor: theme.colors.surface },
  tableCell: {
    flex: 1,
    padding: theme.spacing(0.75),
    fontSize: theme.font.sm,
    color: theme.colors.text,
    borderWidth: StyleSheet.hairlineWidth,
    borderColor: theme.colors.border,
  },
  tableHeaderCell: { fontWeight: '700' },
});
