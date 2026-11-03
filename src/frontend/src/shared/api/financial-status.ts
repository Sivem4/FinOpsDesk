import { requestLocalSessionRevalidation } from '../auth/session-coordination';
import { sessionStore } from './session-store';

let forbiddenKey: string | null = null;
let unauthorizedKey: string | null = null;

export function noteFinancialForbidden(): void {
  const key = observationKey();

  if (forbiddenKey === key) {
    return;
  }

  forbiddenKey = key;
  requestLocalSessionRevalidation();
}

export function noteFinancialUnauthorized(): void {
  const key = observationKey();

  if (unauthorizedKey === key) {
    return;
  }

  unauthorizedKey = key;
  requestLocalSessionRevalidation();
}

export function resetFinancialStatusForTests(): void {
  forbiddenKey = null;
  unauthorizedKey = null;
}

function observationKey(): string {
  const session = sessionStore.getSnapshot();
  return `${session.epoch}:${session.readiness}`;
}
