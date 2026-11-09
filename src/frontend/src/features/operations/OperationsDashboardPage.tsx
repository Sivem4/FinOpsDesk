import { Alert, Button, Stack, Typography } from '@mui/material';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useMemo } from 'react';
import { Link as RouterLink } from 'react-router';

import { ApiError } from '../../shared/api/errors';
import { browserAuthMode } from '../../shared/auth/auth-mode';
import { useSession } from '../../shared/auth/session';
import { clearSummaryCountsForAccessDenial, getReviewCaseSummary } from '../review-cases/review-case-summary-api';
import { getAllocationDisputeWorkSummary } from '../allocation-disputes/allocation-dispute-work-api';

export function OperationsDashboardPage() {
  const session = useSession();
  const cookie = browserAuthMode() === 'cookie';
  const reviewReadable = cookie
    ? session.authorization === 'verified' && session.capabilities.includes('review-cases.read')
    : session.token !== null;
  const disputeReadable = !cookie && session.token !== null;

  return (
    <Stack spacing={2}>
      <Typography variant="h4" component="h1">Operations</Typography>
      {reviewReadable ? <ReviewSection epoch={session.epoch} readiness={session.readiness} cookie={cookie} /> : null}
      {disputeReadable ? <DisputeSection epoch={session.epoch} /> : null}
      {!reviewReadable && !disputeReadable ? (
        <Alert severity="warning">Operations are not available for this session.</Alert>
      ) : null}
    </Stack>
  );
}

function ReviewSection({
  epoch,
  readiness,
  cookie,
}: {
  epoch: number;
  readiness: number;
  cookie: boolean;
}) {
  const queryClient = useQueryClient();
  const queryKey = useMemo(
    () => (cookie
      ? ['review-cases', 'summary', epoch, readiness] as const
      : ['review-cases', 'summary', epoch] as const),
    [cookie, epoch, readiness],
  );
  const summary = useQuery({
    queryKey,
    queryFn: ({ signal }) => getReviewCaseSummary(signal),
  });
  const denial = summary.error instanceof ApiError && (summary.error.status === 401 || summary.error.status === 403)
    ? summary.error
    : null;

  useEffect(() => {
    if (denial !== null) {
      clearSummaryCountsForAccessDenial(queryClient, queryKey, denial);
    }
  }, [denial, queryClient, queryKey]);

  const visible = denial === null ? summary.data : undefined;
  return (
    <Stack component="section" aria-label="Review case queue" spacing={1}>
      <Typography variant="h6" component="h2">Review cases</Typography>
      {visible !== undefined ? (
        <>
          <Typography>{`Open ${visible.openCount}`}</Typography>
          <Button component={RouterLink} to="/review-cases?status=Open">Open review cases</Button>
        </>
      ) : null}
      {denial !== null ? <Alert severity="warning">Review case counts are hidden for this session.</Alert> : null}
    </Stack>
  );
}

function DisputeSection({ epoch }: { epoch: number }) {
  const summary = useQuery({
    queryKey: ['allocation-disputes', 'work-summary', epoch],
    queryFn: ({ signal }) => getAllocationDisputeWorkSummary(signal),
  });

  return (
    <Stack component="section" aria-label="Allocation dispute queues" spacing={1}>
      <Typography variant="h6" component="h2">Allocation disputes</Typography>
      {summary.data !== undefined ? (
        <>
          <Typography>{`Needs decision ${summary.data.needsDecision}`}</Typography>
          <Typography>{`Upheld disputes whose allocation can still be fully reversed ${summary.data.needsCorrection}`}</Typography>
          <Button component={RouterLink} to="/allocation-disputes?queue=needs-decision">Disputes without a decision</Button>
          <Button component={RouterLink} to="/allocation-disputes?queue=needs-correction">Disputes that can be corrected</Button>
        </>
      ) : null}
    </Stack>
  );
}
