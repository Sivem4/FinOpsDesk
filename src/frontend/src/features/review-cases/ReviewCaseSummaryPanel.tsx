import { Alert, Button, Stack, Typography } from '@mui/material';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useMemo, useState } from 'react';

import { ApiError, NetworkError, StaleSessionError, UnexpectedResponseError } from '../../shared/api/errors';
import { browserAuthMode } from '../../shared/auth/auth-mode';
import { endSession, useSession } from '../../shared/auth/session';
import {
  clearSummaryCountsForAccessDenial,
  getReviewCaseSummary,
  type ReviewCaseSummary,
} from './review-case-summary-api';

type AccessStatus = 401 | 403;

type AccessHold = {
  status: AccessStatus;
  key: readonly unknown[];
};

export function ReviewCaseSummaryPanel() {
  const session = useSession();
  const queryClient = useQueryClient();
  const cookie = browserAuthMode() === 'cookie';
  const readable = cookie
    ? session.authorization === 'verified' && session.capabilities.includes('review-cases.read')
    : session.token !== null;
  const queryKey = useMemo(
    () => cookie
      ? ['review-cases', 'summary', session.epoch, session.readiness] as const
      : ['review-cases', 'summary', session.epoch] as const,
    [cookie, session.epoch, session.readiness],
  );
  const summaryQuery = useQuery({
    queryKey,
    enabled: readable,
    queryFn: ({ signal }) => getReviewCaseSummary(signal),
  });
  const [accessHold, setAccessHold] = useState<AccessHold | null>(null);
  const denial = accessStatus(summaryQuery.error);

  if (summaryQuery.isSuccess && accessHold && sameQueryKey(accessHold.key, queryKey)) {
    setAccessHold(null);
  } else if (
    denial !== null
    && (!accessHold || !sameQueryKey(accessHold.key, queryKey) || accessHold.status !== denial)
  ) {
    setAccessHold({ status: denial, key: queryKey });
  }

  useEffect(() => {
    const error = summaryQuery.error;
    if (!(error instanceof ApiError) || accessStatus(error) === null) {
      return;
    }

    clearSummaryCountsForAccessDenial(queryClient, queryKey, error);
  }, [queryClient, queryKey, summaryQuery.error]);

  if (!readable) {
    return null;
  }

  const held = accessHold !== null && sameQueryKey(accessHold.key, queryKey);
  const blocked = held || denial !== null;
  const summary = blocked ? undefined : summaryQuery.data;
  const refreshing = summary !== undefined && summaryQuery.isSuccess && summaryQuery.isFetching;
  const retainedError = summary !== undefined && summaryQuery.isError ? summaryQuery.error : null;

  return (
    <Stack spacing={1} component="section" aria-label="Organization summary">
      <Typography>
        These counts cover every persisted review case in this organization. Filters, the list page, and the selected case do not change them.
      </Typography>
      {summary ? <SummaryCounts summary={summary} /> : null}
      {refreshing ? (
        <Alert severity="info" role="status">
          Refreshing the organization summary. The counts on screen are the last successful read and can be out of date.
        </Alert>
      ) : null}
      {retainedError ? (
        <Alert
          severity="warning"
          action={<RefreshButton onRefresh={() => void summaryQuery.refetch()} refreshing={summaryQuery.isFetching} />}
        >
          The organization summary could not be refreshed. The counts shown are from the last successful read.
        </Alert>
      ) : null}
      {summary && !refreshing && !retainedError ? (
        <RefreshButton onRefresh={() => void summaryQuery.refetch()} refreshing={summaryQuery.isFetching} />
      ) : null}
      {!summary && readable && summaryQuery.isPending && !held ? (
        <Alert severity="info" role="status">Loading the organization summary</Alert>
      ) : null}
      {!summary && (held || denial !== null) ? (
        <AccessAlert
          status={held && accessHold ? accessHold.status : denial ?? 403}
          cookie={cookie}
          onRefresh={() => void summaryQuery.refetch()}
          refreshing={summaryQuery.isFetching}
        />
      ) : null}
      {!summary && !held && denial === null && summaryQuery.isError ? (
        <SummaryFailure
          error={summaryQuery.error}
          onRefresh={() => void summaryQuery.refetch()}
          refreshing={summaryQuery.isFetching}
        />
      ) : null}
    </Stack>
  );
}

function SummaryCounts({ summary }: { summary: ReviewCaseSummary }) {
  const empty = summary.totalCount === '0'
    && summary.openCount === '0'
    && summary.resolvedCount === '0'
    && summary.dismissedCount === '0'
    && summary.openCompetingMatchCount === '0'
    && summary.openNeedsInvestigationCount === '0';

  return (
    <Stack spacing={0.5}>
      <Typography>{`All cases ${summary.totalCount}`}</Typography>
      <Typography>{`Open ${summary.openCount}`}</Typography>
      <Typography>{`Resolved ${summary.resolvedCount}`}</Typography>
      <Typography>{`Dismissed ${summary.dismissedCount}`}</Typography>
      <Typography>{`Open competing matches ${summary.openCompetingMatchCount}`}</Typography>
      <Typography>{`Open investigations ${summary.openNeedsInvestigationCount}`}</Typography>
      {empty ? <Typography>This organization has no persisted review cases.</Typography> : null}
    </Stack>
  );
}

function AccessAlert({
  status,
  cookie,
  onRefresh,
  refreshing,
}: {
  status: AccessStatus;
  cookie: boolean;
  onRefresh: () => void;
  refreshing: boolean;
}) {
  if (status === 401) {
    if (cookie) {
      return (
        <Alert severity="warning" action={<RefreshButton onRefresh={onRefresh} refreshing={refreshing} />}>
          The browser session needs to be checked again. The organization summary stays hidden until that check succeeds.
        </Alert>
      );
    }

    return (
      <Alert severity="warning" action={<JwtAccessActions onRefresh={onRefresh} refreshing={refreshing} reason="unauthorized" />}>
        Your access has expired or is no longer valid. Update it to continue.
      </Alert>
    );
  }

  if (cookie) {
    return (
      <Alert severity="warning" action={<RefreshButton onRefresh={onRefresh} refreshing={refreshing} />}>
        The organization summary is not available for this organization.
      </Alert>
    );
  }

  return (
    <Alert severity="warning" action={<JwtAccessActions onRefresh={onRefresh} refreshing={refreshing} reason="forbidden" />}>
      You don’t have access to the organization summary. Check your access details.
    </Alert>
  );
}

function JwtAccessActions({
  onRefresh,
  refreshing,
  reason,
}: {
  onRefresh: () => void;
  refreshing: boolean;
  reason: 'unauthorized' | 'forbidden';
}) {
  return (
    <Stack direction="row" spacing={1}>
      <RefreshButton onRefresh={onRefresh} refreshing={refreshing} />
      <Button color="inherit" onClick={() => endSession(reason)}>Update access</Button>
    </Stack>
  );
}

function SummaryFailure({
  error,
  onRefresh,
  refreshing,
}: {
  error: unknown;
  onRefresh: () => void;
  refreshing: boolean;
}) {
  if (error instanceof StaleSessionError) {
    return null;
  }

  if (error instanceof NetworkError) {
    return (
      <Alert severity="error" action={<RefreshButton onRefresh={onRefresh} refreshing={refreshing} />}>
        We couldn’t connect. Check your connection and try again.
      </Alert>
    );
  }

  if (error instanceof UnexpectedResponseError) {
    return (
      <Alert severity="error" action={<RefreshButton onRefresh={onRefresh} refreshing={refreshing} />}>
        We couldn’t read the organization summary.
      </Alert>
    );
  }

  return (
    <Alert severity="error" action={<RefreshButton onRefresh={onRefresh} refreshing={refreshing} />}>
      The organization summary could not be loaded.
    </Alert>
  );
}

function RefreshButton({ onRefresh, refreshing }: { onRefresh: () => void; refreshing: boolean }) {
  return (
    <Button color="inherit" onClick={onRefresh} disabled={refreshing}>
      Refresh organization summary
    </Button>
  );
}

function accessStatus(error: unknown): AccessStatus | null {
  if (error instanceof ApiError && (error.status === 401 || error.status === 403)) {
    return error.status;
  }

  return null;
}

function sameQueryKey(left: readonly unknown[], right: readonly unknown[]): boolean {
  return left.length === right.length && left.every((part, index) => Object.is(part, right[index]));
}
