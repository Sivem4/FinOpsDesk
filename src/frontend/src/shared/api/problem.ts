import { parse, isLosslessNumber } from 'lossless-json';

import { ApiError } from './errors';

export const rateLimitCode = 'finopsdesk.rate_limit.exceeded';

export function toApiError(status: number, body: string, retryAfter: string | null = null): ApiError {
  const fallback = fallbackTitle(status);
  const parsed = readProblem(body);

  return new ApiError(
    status,
    parsed.code,
    parsed.title ?? fallback,
    safeDetail(parsed.detail),
    readRetryAfter(retryAfter, Date.now()),
  );
}

export function readRetryAfter(header: string | null, nowMs: number): number | null {
  if (header === null) {
    return null;
  }

  const value = header.trim();

  if (value.length === 0) {
    return null;
  }

  if (/^\d+$/u.test(value)) {
    return readDelaySeconds(value);
  }

  const httpDate = readHttpDate(value, nowMs);

  if (httpDate === null || httpDate <= nowMs) {
    return null;
  }

  const seconds = Math.ceil((httpDate - nowMs) / 1000);

  if (!Number.isSafeInteger(seconds) || seconds <= 0) {
    return null;
  }

  return seconds;
}

function readDelaySeconds(value: string): number | null {
  let seconds = 0;

  for (const character of value) {
    seconds = (seconds * 10) + (character.charCodeAt(0) - 48);

    if (!Number.isSafeInteger(seconds)) {
      return null;
    }
  }

  if (seconds <= 0) {
    return null;
  }

  return seconds;
}

const shortDayNames = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'] as const;

const longDayNames = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'] as const;

const monthNumbers: Record<string, number> = {
  Jan: 0,
  Feb: 1,
  Mar: 2,
  Apr: 3,
  May: 4,
  Jun: 5,
  Jul: 6,
  Aug: 7,
  Sep: 8,
  Oct: 9,
  Nov: 10,
  Dec: 11,
};

function readHttpDate(value: string, nowMs: number): number | null {
  const imf = /^(Sun|Mon|Tue|Wed|Thu|Fri|Sat), (\d{2}) (Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec) (\d{4}) (\d{2}):(\d{2}):(\d{2}) GMT$/u.exec(value);

  if (imf) {
    return composeHttpDate(imf[1], Number(imf[2]), imf[3], Number(imf[4]), Number(imf[5]), Number(imf[6]), Number(imf[7]));
  }

  const rfc850 = /^(Sunday|Monday|Tuesday|Wednesday|Thursday|Friday|Saturday), (\d{2})-(Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)-(\d{2}) (\d{2}):(\d{2}):(\d{2}) GMT$/u.exec(value);

  if (rfc850) {
    const shortDay = shortDayNames[longDayNames.indexOf(rfc850[1] as typeof longDayNames[number])];
    const day = Number(rfc850[2]);
    const monthName = rfc850[3];
    const hour = Number(rfc850[5]);
    const minute = Number(rfc850[6]);
    const second = Number(rfc850[7]);
    const year = readRfc850Year(Number(rfc850[4]), day, monthName, hour, minute, second, nowMs);
    return composeHttpDate(shortDay, day, monthName, year, hour, minute, second);
  }

  const asctime = /^(Sun|Mon|Tue|Wed|Thu|Fri|Sat) (Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec) ([ \d]\d) (\d{2}):(\d{2}):(\d{2}) (\d{4})$/u.exec(value);

  if (asctime) {
    return composeHttpDate(asctime[1], Number(asctime[3]), asctime[2], Number(asctime[7]), Number(asctime[4]), Number(asctime[5]), Number(asctime[6]));
  }

  return null;
}

function readRfc850Year(
  twoDigitYear: number,
  day: number,
  monthName: string,
  hour: number,
  minute: number,
  second: number,
  nowMs: number,
): number {
  const now = new Date(nowMs);
  const horizon = Date.UTC(
    now.getUTCFullYear() + 50,
    now.getUTCMonth(),
    now.getUTCDate(),
    now.getUTCHours(),
    now.getUTCMinutes(),
    now.getUTCSeconds(),
    now.getUTCMilliseconds(),
  );
  const horizonDate = new Date(horizon);
  let year = (Math.floor(horizonDate.getUTCFullYear() / 100) * 100) + twoDigitYear;
  const candidate = Date.UTC(year, monthNumbers[monthName], day, hour, minute, second);

  // RFC 9110: compare the whole date with now + 50 years. A later date uses the previous century.
  if (candidate > horizon) {
    year -= 100;
  }

  return year;
}

function composeHttpDate(
  dayName: string,
  day: number,
  monthName: string,
  year: number,
  hour: number,
  minute: number,
  second: number,
): number | null {
  if (hour > 23 || minute > 59 || second > 59) {
    return null;
  }

  const month = monthNumbers[monthName];
  const utc = Date.UTC(year, month, day, hour, minute, second);
  const date = new Date(utc);

  if (
    date.getUTCFullYear() !== year
    || date.getUTCMonth() !== month
    || date.getUTCDate() !== day
    || shortDayNames[date.getUTCDay()] !== dayName
  ) {
    return null;
  }

  return utc;
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
