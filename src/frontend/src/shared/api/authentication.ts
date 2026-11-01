import { browserAuthMode } from '../auth/auth-mode';
import { toApiError } from './problem';
import type { SessionSnapshot } from './session-store';

export function authenticationHeaders(session: SessionSnapshot, method: string): Record<string, string> {
  // `import.meta.env.DEV` is folded to false in a production build, so the bearer branch is removed.
  if (!import.meta.env.DEV || browserAuthMode() === 'cookie') {
    if (method === 'GET' || method === 'HEAD' || session.csrfToken === null) {
      return {};
    }

    return { 'X-CSRF-Token': session.csrfToken };
  }

  if (session.token === null) {
    return {};
  }

  return { Authorization: `Bearer ${session.token}` };
}

export function rejectCookieFinancialRequest(session: SessionSnapshot): void {
  if (browserAuthMode() !== 'cookie') {
    return;
  }

  throw toApiError(session.authenticated ? 403 : 401, '');
}
