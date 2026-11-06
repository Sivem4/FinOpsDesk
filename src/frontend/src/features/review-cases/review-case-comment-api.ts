import { isLosslessNumber } from 'lossless-json';

import { apiGet } from '../../shared/api/http';
import { UnexpectedResponseError } from '../../shared/api/errors';
import { readSafeInt } from '../../shared/money/money';
import { isExactGuid, trimDotNetWhiteSpace } from './review-case-open-request';

const countToken = /^(0|[1-9]\d*)$/;
const maxInt64 = 9223372036854775807n;
const maxOffset = 2147483647n;
const timestampPattern = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.(\d+))?(Z|[+-](\d{2}):(\d{2}))$/;
const ticksPerSecond = 10_000_000n;
const ticksPerMinute = 600_000_000n;
const ticksPerHour = 36_000_000_000n;
const ticksPerDay = 864_000_000_000n;
const maxDateTimeTicks = 3_155_378_975_999_999_999n;
const maximumTextLength = 500;
const maximumActorLength = 256;

export const reviewCaseCommentPageSize = 50;
export const reviewCaseCommentLastPage = 42949673;

export interface ReviewCaseCommentRecord {
  id: string;
  text: string;
  actorId: string;
  recordedAt: string;
}

export interface ReviewCaseCommentList {
  items: ReviewCaseCommentRecord[];
  page: number;
  pageSize: number;
  totalCount: string;
  totalCountValue: bigint;
}

export async function listReviewCaseComments(
  reviewCaseId: string,
  page: number,
  signal: AbortSignal,
): Promise<ReviewCaseCommentList> {
  if (!isExactGuid(reviewCaseId) || !Number.isSafeInteger(page) || page < 1 || page > reviewCaseCommentLastPage) {
    throw new UnexpectedResponseError('The review case comments could not be read.');
  }

  const body = await apiGet(
    `/api/review-cases/${reviewCaseId}/comments?page=${page}&pageSize=${reviewCaseCommentPageSize}`,
    signal,
  );

  try {
    return mapReviewCaseCommentList(body, page, reviewCaseCommentPageSize);
  } catch (error) {
    if (error instanceof UnexpectedResponseError) {
      throw error;
    }

    throw new UnexpectedResponseError('The review case comments could not be read.');
  }
}

export function mapReviewCaseCommentList(
  body: unknown,
  requestedPage: number,
  requestedPageSize: number,
): ReviewCaseCommentList {
  if (!isRecord(body) || !Array.isArray(body.items)) {
    throw new UnexpectedResponseError('The review case comments could not be read.');
  }

  const items = body.items.map(readReviewCaseComment);
  let page: number;
  let pageSize: number;
  try {
    page = readSafeInt(body.page, 'page', 1, 2_147_483_647);
    pageSize = readSafeInt(body.pageSize, 'pageSize', 1, 100);
  } catch {
    throw new UnexpectedResponseError('The review case comments could not be read.');
  }

  if (page !== requestedPage || pageSize !== requestedPageSize) {
    throw new UnexpectedResponseError('The review case comments could not be read.');
  }

  const totalCount = readCount(body.totalCount);
  return {
    items,
    page,
    pageSize,
    totalCount: totalCount.text,
    totalCountValue: totalCount.value,
  };
}

export function readReviewCaseComment(value: unknown): ReviewCaseCommentRecord {
  if (!isRecord(value)) {
    throw new UnexpectedResponseError('The review case comments could not be read.');
  }

  return {
    id: readGuid(value.id),
    text: readPreservedText(value.text, maximumTextLength),
    actorId: readPreservedText(value.actorId, maximumActorLength),
    recordedAt: readTimestamp(value.recordedAt),
  };
}

export function reviewCaseCommentNextEnabled(page: number, itemCount: number, totalCount: bigint): boolean {
  if (!Number.isSafeInteger(page) || page < 1 || page > reviewCaseCommentLastPage) {
    return false;
  }

  if (!Number.isSafeInteger(itemCount) || itemCount < 0) {
    return false;
  }

  const nextOffset = BigInt(page) * BigInt(reviewCaseCommentPageSize);
  const offsetFits = nextOffset <= maxOffset;
  const countIndicatesMore = totalCount > nextOffset;
  const pageIsFull = itemCount === reviewCaseCommentPageSize;
  return offsetFits && (countIndicatesMore || pageIsFull);
}

function readCount(value: unknown): { text: string; value: bigint } {
  if (!isLosslessNumber(value)) {
    throw new UnexpectedResponseError('The review case comments could not be read.');
  }

  const token = value.toString();
  if (!countToken.test(token)) {
    throw new UnexpectedResponseError('The review case comments could not be read.');
  }

  const parsed = BigInt(token);
  if (parsed > maxInt64) {
    throw new UnexpectedResponseError('The review case comments could not be read.');
  }

  return { text: parsed.toString(), value: parsed };
}

function readGuid(value: unknown): string {
  if (typeof value !== 'string' || !isExactGuid(value)) {
    throw new UnexpectedResponseError('The review case comments could not be read.');
  }

  return value;
}

function readPreservedText(value: unknown, maximumLength: number): string {
  if (typeof value !== 'string' || value.length < 1 || value.length > maximumLength) {
    throw new UnexpectedResponseError('The review case comments could not be read.');
  }

  if (trimDotNetWhiteSpace(value) !== value) {
    throw new UnexpectedResponseError('The review case comments could not be read.');
  }

  return value;
}

function readTimestamp(value: unknown): string {
  if (!isCommentTimestamp(value)) {
    throw new UnexpectedResponseError('The review case comments could not be read.');
  }

  return value;
}

export function isReviewCaseTimestamp(value: unknown): value is string {
  return isCommentTimestamp(value);
}

function isCommentTimestamp(value: unknown): value is string {
  if (typeof value !== 'string') {
    return false;
  }

  const match = timestampPattern.exec(value);
  if (match === null) {
    return false;
  }

  const year = Number(match[1]);
  const month = Number(match[2]);
  const day = Number(match[3]);
  const hour = Number(match[4]);
  const minute = Number(match[5]);
  const second = Number(match[6]);
  if (year < 1 || year > 9999 || hour > 23 || minute > 59 || second > 59 || !isRealCalendarDay(year, month, day)) {
    return false;
  }

  const fraction = match[7];
  if (fraction !== undefined && fraction.length > 7) {
    return false;
  }

  const zone = match[8];
  if (zone === undefined) {
    return false;
  }

  let offset = 0;
  if (zone !== 'Z') {
    const offsetHour = Number(match[9]);
    const offsetMinute = Number(match[10]);
    if (offsetHour > 14 || offsetMinute > 59 || (offsetHour === 14 && offsetMinute !== 0)) {
      return false;
    }

    offset = (zone.startsWith('-') ? -1 : 1) * (offsetHour * 60 + offsetMinute);
  }

  return isUtcInstantInRange(year, month, day, hour, minute, second, fraction ?? '', offset);
}

function isRealCalendarDay(year: number, month: number, day: number): boolean {
  if (month < 1 || month > 12 || day < 1) {
    return false;
  }

  const lengths = [31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];
  const length = month === 2 && isLeapYear(year) ? 29 : lengths[month - 1];
  return day <= length;
}

function isLeapYear(year: number): boolean {
  return year % 4 === 0 && (year % 100 !== 0 || year % 400 === 0);
}

function isUtcInstantInRange(
  year: number,
  month: number,
  day: number,
  hour: number,
  minute: number,
  second: number,
  fraction: string,
  offsetMinutes: number,
): boolean {
  const yearIndex = year - 1;
  const leaps = Math.floor(yearIndex / 4) - Math.floor(yearIndex / 100) + Math.floor(yearIndex / 400);
  let days = yearIndex * 365 + leaps;
  const lengths = [31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31];
  for (let index = 1; index < month; index += 1) {
    days += lengths[index - 1] ?? 0;
  }

  if (month > 2 && isLeapYear(year)) {
    days += 1;
  }

  days += day - 1;
  const fractionTicks = BigInt(fraction.padEnd(7, '0') || '0');
  const localTicks = BigInt(days) * ticksPerDay
    + BigInt(hour) * ticksPerHour
    + BigInt(minute) * ticksPerMinute
    + BigInt(second) * ticksPerSecond
    + fractionTicks;
  const utcTicks = localTicks - BigInt(offsetMinutes) * ticksPerMinute;
  return utcTicks >= 0n && utcTicks <= maxDateTimeTicks;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value) && !isLosslessNumber(value);
}
