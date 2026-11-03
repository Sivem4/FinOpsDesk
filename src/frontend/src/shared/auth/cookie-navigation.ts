import { screenCapabilities } from './browser-session';

export interface CookieNavLink {
  to: string;
  label: string;
  capability: (typeof screenCapabilities)[number];
}

const catalog: readonly CookieNavLink[] = [
  { to: '/invoices', label: 'Invoices', capability: 'invoices.read' },
  { to: '/bank-transactions', label: 'Bank transactions', capability: 'bank-transactions.read' },
  { to: '/reconciliation', label: 'Reconciliation', capability: 'reconciliation.read' },
  { to: '/allocations', label: 'Allocation history', capability: 'allocations.read' },
];

export function cookieNavLinks(capabilities: readonly string[]): CookieNavLink[] {
  return catalog.filter((link) => capabilities.includes(link.capability));
}

export function firstCookieRoute(capabilities: readonly string[]): string | null {
  return cookieNavLinks(capabilities)[0]?.to ?? null;
}
