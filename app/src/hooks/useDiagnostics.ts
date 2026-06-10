import { useCallback, useEffect, useState } from 'react';
import { getDiagnostics, getHealth, type DiagnosticsReport } from '../api/client';

export type ConnectivityState =
  | { kind: 'loading' }
  | { kind: 'unreachable'; error: string }
  | { kind: 'ready'; report: DiagnosticsReport };

/**
 * Phase 0 connectivity hook: confirms the API is reachable (/health) and then
 * pulls the backend diagnostics (/diagnostics). Re-runnable via `refresh`.
 */
export function useDiagnostics(): { state: ConnectivityState; refresh: () => void } {
  const [state, setState] = useState<ConnectivityState>({ kind: 'loading' });

  const refresh = useCallback(async () => {
    setState({ kind: 'loading' });
    try {
      await getHealth();
      const report = await getDiagnostics();
      setState({ kind: 'ready', report });
    } catch (err) {
      setState({ kind: 'unreachable', error: err instanceof Error ? err.message : String(err) });
    }
  }, []);

  useEffect(() => {
    void refresh();
  }, [refresh]);

  return { state, refresh };
}
