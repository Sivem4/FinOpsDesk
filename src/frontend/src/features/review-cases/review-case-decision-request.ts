import { z } from 'zod';

import { readReviewCaseConcurrencyToken } from './review-case-api';
import {
  isExactGuid,
  normalizeReviewCaseNote,
} from './review-case-open-request';

export const reviewCaseDecisionOutcomes = ['AllocationRecorded', 'NoActionRequired', 'HandledOutside'] as const;

export type ReviewCaseDecisionOutcome = (typeof reviewCaseDecisionOutcomes)[number];
export type ReviewCaseDecisionOperation = 'ReviewCaseResolve' | 'ReviewCaseDismiss';

export interface NormalizedReviewCaseDecision {
  operation: ReviewCaseDecisionOperation;
  caseId: string;
  outcome: ReviewCaseDecisionOutcome | null;
  allocationId: string | null;
  resolutionNote: string | null;
  concurrencyToken: string;
}

export interface ReviewCaseResolveFormValues {
  outcome: ReviewCaseDecisionOutcome;
  allocationId: string;
  resolutionNote: string;
}

export interface ReviewCaseDismissFormValues {
  resolutionNote: string;
}

export const reviewCaseResolveSchema = z.object({
  outcome: z.enum(reviewCaseDecisionOutcomes),
  allocationId: z.string(),
  resolutionNote: z.string().superRefine((value, context) => {
    const note = normalizeReviewCaseNote(value);
    if (!note.ok) {
      context.addIssue({ code: 'custom', message: 'A resolution note can contain at most 500 characters.' });
    }
  }),
}).superRefine((value, context) => {
  if (value.outcome === 'AllocationRecorded') {
    if (!isExactGuid(value.allocationId)) {
      context.addIssue({
        code: 'custom',
        path: ['allocationId'],
        message: 'Enter an allocation id with no surrounding spaces.',
      });
    }

    return;
  }

  if (value.allocationId !== '') {
    context.addIssue({
      code: 'custom',
      path: ['allocationId'],
      message: 'This outcome cannot reference an allocation.',
    });
  }
});

export const reviewCaseDismissSchema = z.object({
  resolutionNote: z.string().superRefine((value, context) => {
    const note = normalizeReviewCaseNote(value);
    if (!note.ok) {
      context.addIssue({ code: 'custom', message: 'A resolution note can contain at most 500 characters.' });
    }
  }),
});

export function normalizeReviewCaseResolve(
  caseId: string,
  concurrencyToken: string,
  value: ReviewCaseResolveFormValues,
): NormalizedReviewCaseDecision {
  return normalizeDecision({
    operation: 'ReviewCaseResolve',
    caseId,
    outcome: value.outcome,
    allocationId: value.outcome === 'AllocationRecorded' ? value.allocationId : '',
    resolutionNote: value.resolutionNote,
    concurrencyToken,
  });
}

export function normalizeReviewCaseDismiss(
  caseId: string,
  concurrencyToken: string,
  value: ReviewCaseDismissFormValues,
): NormalizedReviewCaseDecision {
  return normalizeDecision({
    operation: 'ReviewCaseDismiss',
    caseId,
    outcome: null,
    allocationId: '',
    resolutionNote: value.resolutionNote,
    concurrencyToken,
  });
}

export function serializeReviewCaseDecisionBody(value: NormalizedReviewCaseDecision): string {
  if (value.operation === 'ReviewCaseDismiss') {
    const body: Record<string, string> = {};
    if (value.resolutionNote !== null) {
      body.resolutionNote = value.resolutionNote;
    }

    body.concurrencyToken = value.concurrencyToken;
    return JSON.stringify(body);
  }

  const body: Record<string, string> = {
    outcome: value.outcome ?? '',
  };
  if (value.outcome === 'AllocationRecorded' && value.allocationId !== null) {
    body.allocationId = value.allocationId;
  }

  if (value.resolutionNote !== null) {
    body.resolutionNote = value.resolutionNote;
  }

  body.concurrencyToken = value.concurrencyToken;
  return JSON.stringify(body);
}

function normalizeDecision(value: {
  operation: ReviewCaseDecisionOperation;
  caseId: string;
  outcome: ReviewCaseDecisionOutcome | null;
  allocationId: string;
  resolutionNote: string;
  concurrencyToken: string;
}): NormalizedReviewCaseDecision {
  if (!isExactGuid(value.caseId)) {
    throw new Error('The review case id is not valid.');
  }

  const token = readReviewCaseConcurrencyToken(value.concurrencyToken);
  if (token !== value.concurrencyToken) {
    throw new Error('The review case concurrency token is not valid.');
  }

  const note = normalizeReviewCaseNote(value.resolutionNote);
  if (!note.ok) {
    throw new Error('A resolution note can contain at most 500 characters.');
  }

  const allocationId = value.operation === 'ReviewCaseResolve' && value.outcome === 'AllocationRecorded'
    ? value.allocationId.toLowerCase()
    : null;
  if (allocationId !== null && !isExactGuid(allocationId)) {
    throw new Error('Enter an allocation id with no surrounding spaces.');
  }

  return {
    operation: value.operation,
    caseId: value.caseId.toLowerCase(),
    outcome: value.operation === 'ReviewCaseResolve' ? value.outcome : null,
    allocationId,
    resolutionNote: note.note,
    concurrencyToken: token,
  };
}
