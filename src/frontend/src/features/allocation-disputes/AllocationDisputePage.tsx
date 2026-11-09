import { zodResolver } from '@hookform/resolvers/zod';
import { Alert, Button, Stack, TextField, Typography } from '@mui/material';
import { useQuery } from '@tanstack/react-query';
import { useEffect } from 'react';
import { Controller, useForm } from 'react-hook-form';
import { useSearchParams } from 'react-router';

import { ApiError, NetworkError, StaleSessionError, UnexpectedResponseError } from '../../shared/api/errors';
import { endSession, useSession } from '../../shared/auth/session';
import {
  allocationDisputeDetailQueryKey,
  allocationDisputeIdsMatch,
  allocationDisputeNextEnabled,
  getAllocationDispute,
  listAllocationDisputes,
  readAllocationDisputeAddress,
  type AllocationDisputeList,
  type AllocationDisputeRecord,
} from './allocation-dispute-api';
import {
  allocationDisputeFilterSchema,
  type AllocationDisputeFilterValues,
} from './allocation-dispute-filters';
import { listAllocationDisputeQueue } from './allocation-dispute-work-api';
import { AllocationDisputeCorrectionPanel } from './AllocationDisputeCorrectionPanel';
import { AllocationDisputeResolutionPanel } from './AllocationDisputeResolutionPanel';

export function AllocationDisputePage() {
  const session = useSession();
  const [params, setSearchParams] = useSearchParams();
  const address = readAllocationDisputeAddress(params);
  const readable = session.token !== null;
  const selectedId = address.selectedState === 'valid' ? address.selectedValues[0] ?? null : null;
  const listQuery = useQuery({
    queryKey: ['allocation-disputes', address.queue === null ? 'list' : 'queue', session.epoch, address.queue, address.page, address.allocationId],
    enabled: readable && address.listValid,
    queryFn: ({ signal }) => {
      if (address.page === null) {
        throw new UnexpectedResponseError('The allocation disputes could not be read.');
      }

      return address.queue === null
        ? listAllocationDisputes(address.page, address.allocationId, signal)
        : listAllocationDisputeQueue(address.queue, address.page, signal);
    },
  });
  const detailQuery = useQuery({
    queryKey: allocationDisputeDetailQueryKey(session.epoch, selectedId ?? ''),
    enabled: readable && selectedId !== null,
    queryFn: ({ signal }) => {
      if (selectedId === null) {
        throw new UnexpectedResponseError('The allocation dispute could not be read.');
      }

      return getAllocationDispute(selectedId, signal);
    },
  });

  function writeAddress(
    page: number,
    allocationId: string | null,
    selectedValues: readonly string[],
    queue: 'needs-decision' | 'needs-correction' | null,
  ) {
    const next = new URLSearchParams();
    next.set('page', String(page));
    if (queue !== null) {
      next.set('queue', queue);
    }

    if (allocationId !== null) {
      next.set('allocationId', allocationId);
    }

    for (const value of selectedValues) {
      next.append('selected', value);
    }

    setSearchParams(next, { replace: false });
  }

  return (
    <Stack spacing={2}>
      <Typography variant="h4" component="h1">Allocation disputes</Typography>
      <Typography color="text.secondary">
        These are stored dispute registrations. A later reversal does not remove one or change what it recorded.
      </Typography>
      {!readable ? <Alert severity="warning">Allocation disputes are not available for this session.</Alert> : null}
      {readable && !address.listValid ? (
        <Alert severity="warning">This allocation dispute address can’t be opened.</Alert>
      ) : null}
      {readable && address.listValid ? (
        <AllocationDisputeFilter
          allocationId={address.allocationId ?? ''}
          onApply={(allocationId) => {
            writeAddress(1, allocationId, address.selectedValues, null);
          }}
        />
      ) : null}
      {readable && address.listValid && address.queue !== null ? (
        <Stack spacing={1}>
          <Typography>
            This queue is not filtered by allocation. Applying the allocation filter leaves the queue and opens the register.
          </Typography>
          <Button
            variant="outlined"
            sx={{ alignSelf: 'flex-start' }}
            onClick={() => {
              writeAddress(1, address.allocationId, address.selectedValues, null);
            }}
          >
            Show the full register
          </Button>
        </Stack>
      ) : null}
      {readable && address.selectedState === 'invalid' ? (
        <Alert severity="warning">
          The selected allocation dispute address is not valid. The list can still be read.
        </Alert>
      ) : null}
      {readable && address.listValid ? (
        <AllocationDisputeList
          query={listQuery}
          allocationId={address.allocationId}
          page={address.page ?? 1}
          selectedId={selectedId}
          onPage={(page) => {
            writeAddress(page, address.allocationId, address.selectedValues, address.queue);
          }}
          onSelect={(disputeId) => {
            writeAddress(address.page ?? 1, address.allocationId, [disputeId], address.queue);
          }}
        />
      ) : null}
      {readable && selectedId !== null ? (
        <AllocationDisputeDetail query={detailQuery} />
      ) : null}
      {readable ? <AllocationDisputeResolutionPanel epoch={session.epoch} disputeId={selectedId} /> : null}
      {readable ? (
        <AllocationDisputeCorrectionPanel epoch={session.epoch} disputeId={selectedId} />
      ) : null}
    </Stack>
  );
}

function AllocationDisputeFilter({
  allocationId,
  onApply,
}: {
  allocationId: string;
  onApply: (allocationId: string | null) => void;
}) {
  const form = useForm<AllocationDisputeFilterValues>({
    resolver: zodResolver(allocationDisputeFilterSchema),
    defaultValues: { allocationId },
  });
  const { reset } = form;

  useEffect(() => {
    reset({ allocationId });
  }, [allocationId, reset]);

  return (
    <Stack
      spacing={1}
      component="form"
      onSubmit={form.handleSubmit((values) => {
        onApply(values.allocationId === '' ? null : values.allocationId);
      })}
    >
      <Controller
        name="allocationId"
        control={form.control}
        render={({ field, fieldState }) => (
          <TextField
            label="Allocation id"
            value={field.value}
            onChange={field.onChange}
            onBlur={field.onBlur}
            error={fieldState.error !== undefined}
            helperText={fieldState.error?.message}
          />
        )}
      />
      <Button type="submit" variant="contained" sx={{ alignSelf: 'flex-start' }}>Apply filter</Button>
    </Stack>
  );
}

function AllocationDisputeList({
  query,
  allocationId,
  page,
  selectedId,
  onPage,
  onSelect,
}: {
  query: ListQuery;
  allocationId: string | null;
  page: number;
  selectedId: string | null;
  onPage: (page: number) => void;
  onSelect: (disputeId: string) => void;
}) {
  if (query.isLoading) {
    return <Alert severity="info">Loading allocation disputes</Alert>;
  }

  if (query.isError) {
    return (
      <DisputeQueryError
        error={query.error}
        onRetry={() => {
          void query.refetch();
        }}
        retrying={query.isFetching}
        notFound={null}
      />
    );
  }

  if (query.data === undefined) {
    return null;
  }

  const data = query.data;
  const nextEnabled = allocationDisputeNextEnabled(page, data.items.length, data.totalCountValue);
  const noRows = data.items.length === 0 && data.totalCountValue === 0n;

  return (
    <Stack spacing={1}>
      <Typography>{`Dispute page ${data.page}`}</Typography>
      <Typography>{`Dispute count ${data.totalCount}`}</Typography>
      <Stack direction="row" spacing={1}>
        <Button variant="outlined" disabled={page <= 1 || query.isFetching} onClick={() => onPage(page - 1)}>
          Previous page
        </Button>
        <Button variant="outlined" disabled={!nextEnabled || query.isFetching} onClick={() => onPage(page + 1)}>
          Next page
        </Button>
      </Stack>
      {noRows ? (
        <Alert severity="info">
          {allocationId === null
            ? 'No allocation disputes are stored for this organization.'
            : 'No allocation disputes are stored for this allocation.'}
        </Alert>
      ) : null}
      {data.items.length === 0 && data.totalCountValue > 0n ? (
        <Alert severity="info">
          {`This page has no stored allocation disputes. Dispute count ${data.totalCount}.`}
        </Alert>
      ) : null}
      <Stack component="ul" aria-label="Stored allocation disputes" spacing={1} sx={{ m: 0, pl: 2 }}>
        {data.items.map((item, index) => (
          <Stack key={`${item.disputeId}-${index}`} component="li" spacing={0.5}>
            <Typography sx={{ whiteSpace: 'pre-wrap' }}>{item.statement}</Typography>
            <Typography>{`Actor source ${item.actorSource}`}</Typography>
            <Typography sx={{ whiteSpace: 'pre-wrap' }}>{item.actorId}</Typography>
            <Typography>{item.recordedAt}</Typography>
            <Typography>{item.allocationId}</Typography>
            <Typography>{item.disputeId}</Typography>
            <Button
              variant="text"
              aria-pressed={selectedId !== null && allocationDisputeIdsMatch(item.disputeId, selectedId)}
              onClick={() => onSelect(item.disputeId)}
              sx={{ alignSelf: 'flex-start' }}
            >
              {`Show allocation dispute ${item.disputeId}`}
            </Button>
          </Stack>
        ))}
      </Stack>
    </Stack>
  );
}

function AllocationDisputeDetail({ query }: { query: DetailQuery }) {
  return (
    <Stack spacing={1} component="section" aria-label="Allocation dispute registration">
      <Typography variant="h6" component="h2">Selected registration</Typography>
      {query.isLoading ? <Alert severity="info">Loading the selected allocation dispute</Alert> : null}
      {query.isError ? (
        <DisputeQueryError
          error={query.error}
          onRetry={() => {
            void query.refetch();
          }}
          retrying={query.isFetching}
          notFound="That allocation dispute was not found."
        />
      ) : null}
      {query.isSuccess && query.data !== undefined ? <AllocationDisputeFields record={query.data} /> : null}
    </Stack>
  );
}

function AllocationDisputeFields({ record }: { record: AllocationDisputeRecord }) {
  return (
    <Stack spacing={0.5}>
      <Typography sx={{ whiteSpace: 'pre-wrap' }}>{record.statement}</Typography>
      <Typography>{`Actor source ${record.actorSource}`}</Typography>
      <Typography sx={{ whiteSpace: 'pre-wrap' }}>{record.actorId}</Typography>
      <Typography>{record.recordedAt}</Typography>
      <Typography>{record.allocationId}</Typography>
      <Typography>{record.disputeId}</Typography>
    </Stack>
  );
}

function DisputeQueryError({
  error,
  onRetry,
  retrying,
  notFound,
}: {
  error: unknown;
  onRetry: () => void;
  retrying: boolean;
  notFound: string | null;
}) {
  if (error instanceof StaleSessionError) {
    return null;
  }

  if (error instanceof ApiError && error.status === 401) {
    return (
      <Alert
        severity="warning"
        action={(
          <Button color="inherit" onClick={() => endSession('unauthorized')}>
            Update access
          </Button>
        )}
      >
        Your access has expired or is no longer valid. Update it to continue.
      </Alert>
    );
  }

  if (error instanceof ApiError && error.status === 403) {
    return (
      <Alert severity="warning" action={<RetryButton onRetry={onRetry} retrying={retrying} />}>
        You don’t have access to allocation disputes.
      </Alert>
    );
  }

  if (notFound !== null && error instanceof ApiError && error.status === 404) {
    return <Alert severity="warning">{notFound}</Alert>;
  }

  if (error instanceof NetworkError) {
    return (
      <Alert severity="error" action={<RetryButton onRetry={onRetry} retrying={retrying} />}>
        We couldn’t connect. Check your connection and try again.
      </Alert>
    );
  }

  if (error instanceof UnexpectedResponseError) {
    return (
      <Alert severity="error" action={<RetryButton onRetry={onRetry} retrying={retrying} />}>
        We couldn’t read this allocation dispute response.
      </Alert>
    );
  }

  return (
    <Alert severity="error" action={<RetryButton onRetry={onRetry} retrying={retrying} />}>
      Allocation disputes could not be loaded.
    </Alert>
  );
}

function RetryButton({ onRetry, retrying }: { onRetry: () => void; retrying: boolean }) {
  return (
    <Button color="inherit" onClick={onRetry} disabled={retrying}>
      Try again
    </Button>
  );
}

type ListQuery = ReturnType<typeof useQuery<AllocationDisputeList, Error>>;
type DetailQuery = ReturnType<typeof useQuery<AllocationDisputeRecord, Error>>;
