import { isLosslessNumber } from 'lossless-json';

import { apiGet } from '../../shared/api/http';
import { UnexpectedResponseError } from '../../shared/api/errors';
import {
  mapAllocationDisputeList,
  mapAllocationDisputeQueueCursor,
  type AllocationDisputeList,
  type AllocationDisputeQueueCursorPage,
} from './allocation-dispute-api';

const countToken = /^(0|[1-9]\d*)$/;

export const allocationDisputeWorkSummaryFreshMs = 30_000;

export interface AllocationDisputeWorkSummary {
  needsDecision: string;
  needsCorrection: string;
}

export function allocationDisputeWorkSummaryQueryKey(epoch: number) {
  return ['allocation-disputes', 'work-summary', epoch] as const;
}

export function allocationDisputeQueueQueryKey(
  epoch: number,
  queue: 'needs-decision' | 'needs-correction',
  position: { mode: 'offset'; page: number } | { mode: 'cursor'; cursor: string | null },
) {
  return position.mode === 'offset'
    ? ['allocation-disputes', 'queue', epoch, queue, 'offset', position.page] as const
    : ['allocation-disputes', 'queue', epoch, queue, 'cursor', position.cursor ?? 'first'] as const;
}

export async function getAllocationDisputeWorkSummary(signal: AbortSignal): Promise<AllocationDisputeWorkSummary> {
  const body = await apiGet('/api/allocation-disputes/summary', signal);
  return mapSummary(body);
}

export async function listAllocationDisputeQueue(
  queue: 'needs-decision' | 'needs-correction',
  page: number,
  signal: AbortSignal,
): Promise<AllocationDisputeList> {
  const body = await apiGet(`/api/allocation-disputes/queue?queue=${queue}&page=${page}`, signal);
  return mapAllocationDisputeList(body, page, null);
}

export async function listAllocationDisputeQueueCursor(
  queue: 'needs-decision' | 'needs-correction',
  cursor: string | null,
  signal: AbortSignal,
): Promise<AllocationDisputeQueueCursorPage> {
  const query = new URLSearchParams({ queue, pagination: 'cursor' });
  if (cursor !== null) {
    query.set('cursor', cursor);
  }

  const body = await apiGet(`/api/allocation-disputes/queue?${query.toString()}`, signal);
  return mapAllocationDisputeQueueCursor(body);
}

function mapSummary(body: unknown): AllocationDisputeWorkSummary {
  if (!isRecord(body)) {
    throw new UnexpectedResponseError('The allocation dispute summary could not be read.');
  }

  return {
    needsDecision: readCount(body.needsDecision),
    needsCorrection: readCount(body.needsCorrection),
  };
}

function readCount(value: unknown): string {
  const text = isLosslessNumber(value) ? value.toString() : typeof value === 'string' ? value : null;
  if (text === null || !countToken.test(text)) {
    throw new UnexpectedResponseError('The allocation dispute summary could not be read.');
  }

  return text;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value) && !isLosslessNumber(value);
}
