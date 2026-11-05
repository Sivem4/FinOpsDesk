import { z } from 'zod';

const guidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const emptyGuid = '00000000-0000-0000-0000-000000000000';
const maximumNoteLength = 500;
const maximumActorLength = 256;

export const reviewCaseOpenReasons = ['CompetingMatch', 'NeedsInvestigation'] as const;
export const reviewCaseOpenSources = ['manual', 'exact-1', 'smart-1'] as const;

export type ReviewCaseOpenReason = (typeof reviewCaseOpenReasons)[number];
export type ReviewCaseOpenSource = (typeof reviewCaseOpenSources)[number];

export interface NormalizedReviewCaseOpen {
  reason: ReviewCaseOpenReason;
  invoiceId: string | null;
  bankTransactionId: string | null;
  sourceLabel: ReviewCaseOpenSource | null;
  note: string | null;
}

export interface ReviewCaseOpenFormValues {
  reason: ReviewCaseOpenReason;
  invoiceId: string;
  bankTransactionId: string;
  sourceLabel: '' | ReviewCaseOpenSource;
  note: string;
}

export const reviewCaseOpenSchema = z.object({
  reason: z.enum(reviewCaseOpenReasons),
  invoiceId: z.string().refine(isEmptyOrExactGuid, 'Enter an invoice id with no surrounding spaces.'),
  bankTransactionId: z.string().refine(isEmptyOrExactGuid, 'Enter a bank transaction id with no surrounding spaces.'),
  sourceLabel: z.enum(['', ...reviewCaseOpenSources]),
  note: z.string().superRefine((value, context) => {
    const note = normalizeReviewCaseNote(value);
    if (!note.ok) {
      context.addIssue({ code: 'custom', message: note.message });
    }
  }),
}).superRefine((value, context) => {
  const invoice = value.invoiceId !== '';
  const bank = value.bankTransactionId !== '';
  if (value.reason === 'CompetingMatch' && (!invoice || !bank)) {
    context.addIssue({
      code: 'custom',
      message: 'A competing match needs both an invoice id and a bank transaction id.',
      path: ['invoiceId'],
    });
  }

  if (value.reason === 'NeedsInvestigation' && invoice === bank) {
    context.addIssue({
      code: 'custom',
      message: 'An investigation needs exactly one document id.',
      path: ['invoiceId'],
    });
  }
});

export function normalizeReviewCaseOpen(value: ReviewCaseOpenFormValues): NormalizedReviewCaseOpen {
  const note = normalizeReviewCaseNote(value.note);
  if (!note.ok) {
    throw new Error(note.message);
  }

  return {
    reason: value.reason,
    invoiceId: value.invoiceId === '' ? null : value.invoiceId.toLowerCase(),
    bankTransactionId: value.bankTransactionId === '' ? null : value.bankTransactionId.toLowerCase(),
    sourceLabel: value.sourceLabel === '' ? null : value.sourceLabel,
    note: note.note,
  };
}

export function serializeReviewCaseOpenBody(value: NormalizedReviewCaseOpen): string {
  const body: Record<string, string> = { reason: value.reason };
  if (value.invoiceId !== null) {
    body.invoiceId = value.invoiceId;
  }

  if (value.bankTransactionId !== null) {
    body.bankTransactionId = value.bankTransactionId;
  }

  if (value.sourceLabel !== null) {
    body.sourceLabel = value.sourceLabel;
  }

  if (value.note !== null) {
    body.note = value.note;
  }

  return JSON.stringify(body);
}

export function normalizeReviewCaseNote(value: string): { ok: true; note: string | null } | { ok: false; message: string } {
  if (isDotNetNullOrWhiteSpace(value)) {
    return { ok: true, note: null };
  }

  const note = trimDotNetWhiteSpace(value);
  if (note.length > maximumNoteLength) {
    return { ok: false, message: 'An opening note can contain at most 500 characters.' };
  }

  return { ok: true, note };
}

export function developmentActorId(token: string | null): string | null {
  const claim = readClaim(token, 'sub');
  if (typeof claim !== 'string' || isDotNetNullOrWhiteSpace(claim)) {
    return null;
  }

  const actorId = trimDotNetWhiteSpace(claim);
  if (actorId.length === 0 || actorId.length > maximumActorLength) {
    return null;
  }

  return actorId;
}

export function developmentOrganizationId(token: string | null): string | null {
  const claim = readClaim(token, 'org_id');
  if (typeof claim !== 'string' || !isExactGuid(claim)) {
    return null;
  }

  return claim.toLowerCase();
}

export function isExactGuid(value: string): boolean {
  return guidPattern.test(value) && value.toLowerCase() !== emptyGuid;
}

export function isDotNetNullOrWhiteSpace(value: string): boolean {
  return trimDotNetWhiteSpace(value).length === 0;
}

export function trimDotNetWhiteSpace(value: string): string {
  let start = 0;
  let end = value.length;
  while (start < end && isDotNetWhiteSpace(value.charCodeAt(start))) {
    start += 1;
  }

  while (end > start && isDotNetWhiteSpace(value.charCodeAt(end - 1))) {
    end -= 1;
  }

  return value.slice(start, end);
}

function isEmptyOrExactGuid(value: string): boolean {
  return value === '' || isExactGuid(value);
}

function isDotNetWhiteSpace(code: number): boolean {
  return code === 0x20
    || (code >= 0x09 && code <= 0x0d)
    || code === 0x85
    || code === 0xa0
    || code === 0x1680
    || (code >= 0x2000 && code <= 0x200a)
    || code === 0x2028
    || code === 0x2029
    || code === 0x202f
    || code === 0x205f
    || code === 0x3000;
}

function readClaim(token: string | null, name: string): unknown {
  if (token === null) {
    return null;
  }

  const parts = token.split('.');
  if (parts.length !== 3 || parts.some((part) => part.length === 0)) {
    return null;
  }

  try {
    const payload: unknown = JSON.parse(decodeBase64Url(parts[1]));
    return isRecord(payload) ? payload[name] : null;
  } catch {
    return null;
  }
}

function decodeBase64Url(value: string): string {
  const base64 = value.replace(/-/g, '+').replace(/_/g, '/');
  const padded = base64.padEnd(base64.length + ((4 - (base64.length % 4)) % 4), '=');
  const binary = atob(padded);
  const bytes = Uint8Array.from(binary, (character) => character.charCodeAt(0));
  return new TextDecoder().decode(bytes);
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
