import type { QueryClient } from '@tanstack/react-query';
import { isLosslessNumber } from 'lossless-json';

import { apiGet } from '../../shared/api/http';
import { ApiError, UnexpectedResponseError } from '../../shared/api/errors';

const countToken = /^(0|[1-9]\d*)$/;
const maxInt64 = 9223372036854775807n;

export interface ReviewCaseSummary {
  totalCount: string;
  openCount: string;
  resolvedCount: string;
  dismissedCount: string;
  openCompetingMatchCount: string;
  openNeedsInvestigationCount: string;
}

export async function getReviewCaseSummary(signal: AbortSignal): Promise<ReviewCaseSummary> {
  const body = await apiGet('/api/review-cases/summary', signal);
  return mapSummary(body);
}

export function startReviewCaseSummaryRefresh(queryClient: QueryClient, epoch: number): void {
  const refresh = queryClient.invalidateQueries(
    { queryKey: ['review-cases', 'summary', epoch] },
    { throwOnError: true },
  );
  void refresh.catch(() => undefined);
}

export function clearSummaryCountsForAccessDenial(
  queryClient: QueryClient,
  queryKey: readonly unknown[],
  denial: ApiError,
): void {
  if (denial.status !== 401 && denial.status !== 403) {
    return;
  }

  const query = queryClient.getQueryCache().find({ queryKey, exact: true });
  if (!query || query.state.error !== denial || query.state.status === 'success') {
    return;
  }

  if (query.state.data === undefined) {
    return;
  }

  query.setState({ data: undefined });
}

function mapSummary(body: unknown): ReviewCaseSummary {
  try {
    if (!isRecord(body)) {
      throw new UnexpectedResponseError('The review case summary could not be read.');
    }

    const total = readCount(body.totalCount);
    const open = readCount(body.openCount);
    const resolved = readCount(body.resolvedCount);
    const dismissed = readCount(body.dismissedCount);
    const competing = readCount(body.openCompetingMatchCount);
    const needs = readCount(body.openNeedsInvestigationCount);
    if (total.value !== open.value + resolved.value + dismissed.value) {
      throw new UnexpectedResponseError('The review case summary could not be read.');
    }

    if (open.value !== competing.value + needs.value) {
      throw new UnexpectedResponseError('The review case summary could not be read.');
    }

    return {
      totalCount: total.text,
      openCount: open.text,
      resolvedCount: resolved.text,
      dismissedCount: dismissed.text,
      openCompetingMatchCount: competing.text,
      openNeedsInvestigationCount: needs.text,
    };
  } catch (error) {
    if (error instanceof UnexpectedResponseError) {
      throw error;
    }

    throw new UnexpectedResponseError('The review case summary could not be read.');
  }
}

function readCount(value: unknown): { text: string; value: bigint } {
  if (!isLosslessNumber(value)) {
    throw new UnexpectedResponseError('The review case summary could not be read.');
  }

  const token = value.toString();
  if (!countToken.test(token)) {
    throw new UnexpectedResponseError('The review case summary could not be read.');
  }

  const parsed = BigInt(token);
  if (parsed > maxInt64) {
    throw new UnexpectedResponseError('The review case summary could not be read.');
  }

  return { text: parsed.toString(), value: parsed };
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value) && !isLosslessNumber(value);
}
