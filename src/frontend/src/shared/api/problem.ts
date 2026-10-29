import { parse, isLosslessNumber } from 'lossless-json';

import { ApiError } from './errors';

export function toApiError(status: number, body: string): ApiError {
  const fallback = fallbackTitle(status);
  const parsed = readProblem(body);

  return new ApiError(
    status,
    parsed.code,
    parsed.title ?? fallback,
    safeDetail(parsed.detail),
  );
}

function readProblem(body: string): { title: string | null; detail: string; code: string | null } {
  const trimmed = body.trim();

  if (!trimmed.startsWith('{')) {
    return { title: null, detail: '', code: null };
  }

  try {
    const value = parse(trimmed);

    if (!isRecord(value)) {
      return { title: null, detail: '', code: null };
    }

    return {
      title: typeof value.title === 'string' ? value.title : null,
      detail: typeof value.detail === 'string' ? value.detail : '',
      code: typeof value.code === 'string' ? value.code : null,
    };
  } catch {
    return { title: null, detail: '', code: null };
  }
}

function safeDetail(detail: string): string {
  if (detail.length === 0 || detail.length > 500) {
    return '';
  }

  if (/exception|stack trace|\bat\s+\S+\(/iu.test(detail)) {
    return '';
  }

  return detail;
}

function fallbackTitle(status: number): string {
  if (status === 400) {
    return 'The request was rejected.';
  }

  if (status === 401) {
    return 'Unauthorized';
  }

  if (status === 403) {
    return 'Forbidden';
  }

  if (status === 409) {
    return 'The import conflict was not resolved.';
  }

  if (status === 413) {
    return 'The file is too large.';
  }

  return 'The API could not complete the request.';
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value) && !isLosslessNumber(value);
}
