import { z } from 'zod';

import { trimDotNetWhiteSpace } from './review-case-open-request';

const maximumTextLength = 500;

export interface ReviewCaseCommentFormValues {
  text: string;
}

export const reviewCaseCommentSchema = z.object({
  text: z.string().superRefine((value, context) => {
    const text = normalizeReviewCaseCommentText(value);
    if (!text.ok) {
      context.addIssue({ code: 'custom', message: text.message });
    }
  }),
});

export function normalizeReviewCaseCommentText(
  value: string,
): { ok: true; text: string } | { ok: false; message: string } {
  const text = trimDotNetWhiteSpace(value);
  if (text.length < 1) {
    return {
      ok: false,
      message: 'Enter a comment. Leading and trailing whitespace is removed. A byte-order mark is kept.',
    };
  }

  if (text.length > maximumTextLength) {
    return {
      ok: false,
      message: 'A comment can contain at most 500 characters after whitespace is removed.',
    };
  }

  return { ok: true, text };
}

export function serializeReviewCaseCommentBody(text: string): string {
  return JSON.stringify({ text });
}
