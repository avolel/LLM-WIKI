// Typed client for the LlmWiki.Api backend.
// Base URL comes from config (EXPO_PUBLIC_API_BASE_URL), defaulting to the local dev API.
// Phase 0 scope: prove connectivity via /health and /diagnostics.

export const API_BASE_URL =
  process.env.EXPO_PUBLIC_API_BASE_URL ?? 'http://localhost:5080';

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

async function getJson<T>(path: string): Promise<T> {
  const response = await fetch(`${API_BASE_URL}${path}`);
  // /diagnostics returns 503 with a body when checks fail; still parse it.
  if (!response.ok && response.status !== 503) {
    throw new Error(`Request to ${path} failed: ${response.status} ${response.statusText}`);
  }
  return (await response.json()) as T;
}

/** Liveness check — resolves when the API process is up. */
export function getHealth(): Promise<HealthResponse> {
  return getJson<HealthResponse>('/health');
}

/** Runs the backend Phase 0 connectivity checks (Oracle, embedding, chat). */
export function getDiagnostics(): Promise<DiagnosticsReport> {
  return getJson<DiagnosticsReport>('/diagnostics');
}
