// Typed client for the LlmWiki.Api backend.
// Base URL comes from config (EXPO_PUBLIC_API_BASE_URL), defaulting to the local dev API.
// Phase 8: the full HTTP surface the client needs — diagnostics, projects, query/save, browse.
// The API now emits enums as their string names, so enums here are string unions.

export const API_BASE_URL =
  process.env.EXPO_PUBLIC_API_BASE_URL ?? 'http://localhost:5080';

// ---- shared enums (string, matching the API) ----
export type PageType = 'Summary' | 'Entity' | 'Concept' | 'Overview' | 'Answer';
export type LinkStyle = 'Wikilink' | 'MarkdownLink';

// ---- diagnostics (existing) ----
export interface HealthResponse {
  status: string;
}

export interface DiagnosticCheck {
  name: string;
  passed: boolean;
  detail: string;
}

export interface DiagnosticsReport {
  checks: DiagnosticCheck[];
  allPassed: boolean;
}

// ---- projects ----
export interface ProjectInfo {
  name: string;
  createdAt: string;
  lastIngestAt: string | null;
  pageCount: number;
  sourceCount: number;
}

// ---- query ----
export interface Citation {
  relativePath: string;
  title: string;
  type: PageType;
}
export interface ConversationTurn {
  question: string;
  answer: string;
}
export interface QueryResult {
  wikiName: string;
  question: string;
  answer: string;
  covered: boolean;
  citations: Citation[];
  suggestedTitle: string;
}
export interface PageOutcome {
  relativePath: string;
  title: string;
  change: string;
  detail: string | null;
}

// ---- browse ----
export interface PageRef {
  relativePath: string;
  slug: string;
}
export interface PageCategory {
  name: string;
  pages: PageRef[];
}
export interface WikiTree {
  wikiName: string;
  categories: PageCategory[];
}
export interface WikiPage {
  title: string;
  type: PageType;
  content: string;
  tags: string[];
  sources: string[];
  createdAt: string;
  updatedAt: string;
}

async function getJson<T>(path: string): Promise<T> {
  const response = await fetch(`${API_BASE_URL}${path}`);
  // /diagnostics returns 503 with a body when checks fail; still parse it.
  if (!response.ok && response.status !== 503) {
    throw new Error(`Request to ${path} failed: ${response.status} ${response.statusText}`);
  }
  return (await response.json()) as T;
}

async function postJson<T>(path: string, body: unknown): Promise<T> {
  const response = await fetch(`${API_BASE_URL}${path}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
  if (!response.ok) {
    throw new Error(`Request to ${path} failed: ${response.status} ${response.statusText}`);
  }
  return (await response.json()) as T;
}

// ---- diagnostics ----
/** Liveness check — resolves when the API process is up. */
export function getHealth(): Promise<HealthResponse> {
  return getJson<HealthResponse>('/health');
}

/** Runs the backend Phase 0 connectivity checks (Oracle, embedding, chat). */
export function getDiagnostics(): Promise<DiagnosticsReport> {
  return getJson<DiagnosticsReport>('/diagnostics');
}

// ---- projects ----
export const listProjects = () => getJson<ProjectInfo[]>('/projects');

export const createProject = (name: string, linkStyle?: LinkStyle) =>
  postJson<ProjectInfo>('/projects', { name, linkStyle });

// ---- query ----
export const postQuery = (
  wiki: string,
  question: string,
  history: ConversationTurn[],
  topK = 5,
) => postJson<QueryResult>('/query', { wiki, question, history, topK });

export const saveAnswer = (wiki: string, result: QueryResult) =>
  postJson<PageOutcome>('/query/save', { wiki, result });

// ---- browse ----
export const getWikiTree = (wiki: string) =>
  getJson<WikiTree>(`/wikis/${encodeURIComponent(wiki)}/pages`);

export const getPage = (wiki: string, relativePath: string) =>
  getJson<WikiPage>(
    `/wikis/${encodeURIComponent(wiki)}/pages/` +
      relativePath.split('/').map(encodeURIComponent).join('/'),
  );
  
  async function deleteJson(path: string): Promise<void> {
  const response = await fetch(`${API_BASE_URL}${path}`, { method: 'DELETE' });
  if (!response.ok) {
    throw new Error(`Request to ${path} failed: ${response.status} ${response.statusText}`);
  }
}

// ---- projects ----
export const deleteProject = (name: string) =>
  deleteJson(`/projects/${encodeURIComponent(name)}`);
