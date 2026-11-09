import { isLosslessNumber } from 'lossless-json';

import { apiGet } from '../../shared/api/http';
import { UnexpectedResponseError } from '../../shared/api/errors';
import { mapAllocationDisputeList, type AllocationDisputeList } from './allocation-dispute-api';

const countToken = /^(0|[1-9]\d*)$/;

export interface AllocationDisputeWorkSummary {
  needsDecision: string;
  needsCorrection: string;
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
