import { isLosslessNumber } from 'lossless-json';

import { apiGet } from '../../shared/api/http';
import { UnexpectedResponseError } from '../../shared/api/errors';
import { readSafeInt } from '../../shared/money/money';

const countToken = /^(0|[1-9]\d*)$/;
const pageToken = /^[1-9]\d*$/;
const guidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const emptyGuid = '00000000-0000-0000-0000-000000000000';
const maxInt64 = 9223372036854775807n;
const maxOffset = 2147483647n;
const timestampPattern = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.(\d+))?(Z|[+-](\d{2}):(\d{2}))$/;
const ticksPerSecond = 10_000_000n;
const ticksPerMinute = 600_000_000n;
const ticksPerHour = 36_000_000_000n;
const ticksPerDay = 864_000_000_000n;
const maxDateTimeTicks = 3_155_378_975_999_999_999n;
const maximumStatementLength = 500;
const maximumActorLength = 256;
const invalidList = 'The allocation disputes could not be read.';
const invalidDetail = 'The allocation dispute could not be read.';
const listKeys = ['items', 'page', 'pageSize', 'totalCount'];
const disputeKeys = ['disputeId', 'allocationId', 'statement', 'actorSource', 'actorId', 'recordedAt'];

export const allocationDisputePageSize = 50;
export const allocationDisputeLastPage = 42949673;

export interface AllocationDisputeRecord {
  disputeId: string;
  allocationId: string;
  statement: string;
  actorSource: string;
  actorId: string;
  recordedAt: string;
}

export interface AllocationDisputeList {
  items: AllocationDisputeRecord[];
  page: number;
  pageSize: number;
  totalCount: string;
  totalCountValue: bigint;
}

export interface AllocationDisputeAddress {
  listValid: boolean;
  page: number | null;
  allocationId: string | null;
  selectedValues: readonly string[];
  selectedState: 'absent' | 'valid' | 'invalid';
}

export function isExactGuid(value: string): boolean {
  return guidPattern.test(value) && value.toLowerCase() !== emptyGuid;
}

export function allocationDisputeIdsMatch(left: string, right: string): boolean {
  return isExactGuid(left) && isExactGuid(right) && left.toLowerCase() === right.toLowerCase();
}

export function readAllocationDisputeAddress(params: URLSearchParams): AllocationDisputeAddress {
  const page = readPage(params);
  const allocation = readGuidParameter(params, 'allocationId');
  const selected = readGuidParameter(params, 'selected');
  return {
    listValid: page !== 'invalid' && allocation.state !== 'invalid',
    page: page === 'invalid' ? null : page,
    allocationId: allocation.state === 'valid' ? allocation.value : null,
    selectedValues: selected.state === 'absent' ? [] : selected.values,
    selectedState: selected.state === 'valid' ? 'valid' : selected.state,
  };
}

export function allocationDisputeNextEnabled(page: number, itemCount: number, totalCount: bigint): boolean {
  if (!Number.isSafeInteger(page) || page < 1 || page > allocationDisputeLastPage) {
    return false;
  }

  if (!Number.isSafeInteger(itemCount) || itemCount < 0) {
    return false;
  }

  const nextOffset = BigInt(page) * BigInt(allocationDisputePageSize);
  const offsetFits = nextOffset <= maxOffset;
  const countIndicatesMore = totalCount > nextOffset;
  const pageIsFull = itemCount === allocationDisputePageSize;
  return offsetFits && (countIndicatesMore || pageIsFull);
}

export async function listAllocationDisputes(
  page: number,
  allocationId: string | null,
  signal: AbortSignal,
): Promise<AllocationDisputeList> {
  if (!Number.isSafeInteger(page) || page < 1 || page > allocationDisputeLastPage) {
    throw new UnexpectedResponseError(invalidList);
  }

  if (allocationId !== null && !isExactGuid(allocationId)) {
    throw new UnexpectedResponseError(invalidList);
  }

  const query = new URLSearchParams({ page: String(page) });
  if (allocationId !== null) {
    query.set('allocationId', allocationId);
  }

  const body = await apiGet(`/api/allocation-disputes?${query.toString()}`, signal);
  return mapAllocationDisputeList(body, page, allocationId);
}

export async function getAllocationDispute(
  disputeId: string,
  signal: AbortSignal,
): Promise<AllocationDisputeRecord> {
  if (!isExactGuid(disputeId)) {
    throw new UnexpectedResponseError(invalidDetail);
  }

  const body = await apiGet(`/api/allocation-disputes/${disputeId}`, signal);
  return mapAllocationDispute(body, disputeId);
}

export function mapAllocationDisputeList(
  body: unknown,
  requestedPage: number,
  allocationId: string | null,
): AllocationDisputeList {
  if (!Number.isSafeInteger(requestedPage) || requestedPage < 1 || requestedPage > allocationDisputeLastPage) {
    throw new UnexpectedResponseError(invalidList);
  }

  if (allocationId !== null && !isExactGuid(allocationId)) {
    throw new UnexpectedResponseError(invalidList);
  }

  if (!isRecord(body) || !hasExactKeys(body, listKeys) || !Array.isArray(body.items)) {
    throw new UnexpectedResponseError(invalidList);
  }

  const items = body.items.map((item) => readDispute(item, invalidList));
  const page = readEcho(body.page, 'page', 1, 2_147_483_647);
  const pageSize = readEcho(body.pageSize, 'pageSize', 1, 100);
  if (page !== requestedPage || pageSize !== allocationDisputePageSize) {
    throw new UnexpectedResponseError(invalidList);
  }

  if (allocationId !== null && items.some((item) => !allocationDisputeIdsMatch(item.allocationId, allocationId))) {
    throw new UnexpectedResponseError(invalidList);
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

export function mapAllocationDispute(body: unknown, requestedDisputeId: string): AllocationDisputeRecord {
  if (!isExactGuid(requestedDisputeId)) {
    throw new UnexpectedResponseError(invalidDetail);
  }

  const record = readDispute(body, invalidDetail);
  if (!allocationDisputeIdsMatch(record.disputeId, requestedDisputeId)) {
    throw new UnexpectedResponseError(invalidDetail);
  }

  return record;
}

function readPage(params: URLSearchParams): number | 'invalid' {
  const values = params.getAll('page');
  if (values.length === 0) {
    return 1;
  }

  if (values.length !== 1 || !pageToken.test(values[0] ?? '')) {
    return 'invalid';
  }

  const parsed = BigInt(values[0] ?? '');
  if (parsed < 1n || parsed > BigInt(allocationDisputeLastPage)) {
    return 'invalid';
  }

  return Number(parsed);
}

function readGuidParameter(
  params: URLSearchParams,
  name: string,
): { state: 'absent' } | { state: 'invalid'; values: string[] } | { state: 'valid'; value: string; values: string[] } {
  const values = params.getAll(name);
  if (values.length === 0) {
    return { state: 'absent' };
  }

  const value = values[0] ?? '';
  if (values.length !== 1 || !isExactGuid(value)) {
    return { state: 'invalid', values };
  }

  return { state: 'valid', value, values };
}

function readDispute(value: unknown, failure: string): AllocationDisputeRecord {
  if (!isRecord(value) || !hasExactKeys(value, disputeKeys)) {
    throw new UnexpectedResponseError(failure);
  }

  return {
    disputeId: readGuid(value.disputeId, failure),
    allocationId: readGuid(value.allocationId, failure),
    statement: readPreservedText(value.statement, maximumStatementLength, failure),
    actorSource: readActorSource(value.actorSource, failure),
    actorId: readPreservedText(value.actorId, maximumActorLength, failure),
    recordedAt: readTimestamp(value.recordedAt, failure),
  };
}

function readCount(value: unknown): { text: string; value: bigint } {
  if (!isLosslessNumber(value)) {
    throw new UnexpectedResponseError(invalidList);
  }

  const token = value.toString();
  if (!countToken.test(token)) {
    throw new UnexpectedResponseError(invalidList);
  }

  const parsed = BigInt(token);
  if (parsed > maxInt64) {
    throw new UnexpectedResponseError(invalidList);
  }

  return { text: parsed.toString(), value: parsed };
}

function readEcho(value: unknown, name: string, minimum: number, maximum: number): number {
  try {
    return readSafeInt(value, name, minimum, maximum);
  } catch {
    throw new UnexpectedResponseError(invalidList);
  }
}

function readGuid(value: unknown, failure: string): string {
  if (typeof value !== 'string' || !isExactGuid(value)) {
    throw new UnexpectedResponseError(failure);
  }

  return value;
}

function readActorSource(value: unknown, failure: string): string {
  if (value !== 'developmentJwt' && value !== 'productionCookie') {
    throw new UnexpectedResponseError(failure);
  }

  return value;
}

function readPreservedText(value: unknown, maximumLength: number, failure: string): string {
  if (typeof value !== 'string' || value.length < 1 || value.length > maximumLength) {
    throw new UnexpectedResponseError(failure);
  }

  if (trimDotNetWhiteSpace(value) !== value) {
    throw new UnexpectedResponseError(failure);
  }

  return value;
}

function readTimestamp(value: unknown, failure: string): string {
  if (!isDisputeTimestamp(value)) {
    throw new UnexpectedResponseError(failure);
  }

  return value;
}

function trimDotNetWhiteSpace(value: string): string {
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

function isDisputeTimestamp(value: unknown): value is string {
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

function hasExactKeys(value: Record<string, unknown>, keys: readonly string[]): boolean {
  const actual = Object.keys(value);
  return actual.length === keys.length && keys.every((key) => actual.includes(key));
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value) && !isLosslessNumber(value);
}
