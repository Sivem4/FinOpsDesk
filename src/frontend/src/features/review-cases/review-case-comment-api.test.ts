import { parse } from 'lossless-json';
import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';

import { UnexpectedResponseError } from '../../shared/api/errors';
import { sessionStore } from '../../shared/api/session-store';
import { server } from '../../test/server';
import { fakeJwt } from '../../test/jwt';
import {
  listReviewCaseComments,
  mapReviewCaseCommentList,
  reviewCaseCommentLastPage,
  reviewCaseCommentNextEnabled,
} from './review-case-comment-api';
import { normalizeReviewCaseCommentText } from './review-case-comment-request';

const caseId = '11111111-1111-4111-8111-111111111111';
const commentId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaa1';

describe('review case comment text', () => {
  it('normalizes .NET whitespace after the raw value and keeps U+FEFF', () => {
    expect(normalizeReviewCaseCommentText('  kept  ')).toEqual({ ok: true, text: 'kept' });
    expect(normalizeReviewCaseCommentText(` ${'a'.repeat(500)} `)).toEqual({ ok: true, text: 'a'.repeat(500) });
    expect(normalizeReviewCaseCommentText('\uFEFF')).toEqual({ ok: true, text: '\uFEFF' });
    expect(normalizeReviewCaseCommentText(' \uFEFFkept\uFEFF ')).toEqual({ ok: true, text: '\uFEFFkept\uFEFF' });
    expect(normalizeReviewCaseCommentText(' \n ').ok).toBe(false);
    expect(normalizeReviewCaseCommentText(` ${'a'.repeat(501)} `).ok).toBe(false);
  });
});

describe('review case comment pagination', () => {
  it('follows the count, the full page, and the offset limit', () => {
    expect(reviewCaseCommentNextEnabled(1, 0, 0n)).toBe(false);
    expect(reviewCaseCommentNextEnabled(1, 3, 3n)).toBe(false);
    expect(reviewCaseCommentNextEnabled(1, 50, 50n)).toBe(true);
    expect(reviewCaseCommentNextEnabled(2, 0, 50n)).toBe(false);
    expect(reviewCaseCommentNextEnabled(1, 2, 120n)).toBe(true);
    expect(reviewCaseCommentNextEnabled(1, 2, 0n)).toBe(false);
    expect(reviewCaseCommentNextEnabled(1, 50, 0n)).toBe(true);
    expect(reviewCaseCommentNextEnabled(reviewCaseCommentLastPage, 50, 9223372036854775807n)).toBe(false);
    expect(reviewCaseCommentNextEnabled(reviewCaseCommentLastPage - 1, 50, 9223372036854775807n)).toBe(true);
  });
});

describe('review case comment parser', () => {
  it('requests only page and pageSize and keeps a count above the safe integer range', async () => {
    let requestUrl = '';
    server.use(http.get('/api/review-cases/:id/comments', ({ request }) => {
      requestUrl = request.url;
      return HttpResponse.text(pageBody([{
        id: commentId,
        text: 'kept',
        actorId: 'original-actor',
        recordedAt: '2026-11-06T12:00:00Z',
      }], '9223372036854775807', 2));
    }));
    sessionStore.rotate(fakeJwt(), 4_000_000_000, null);

    const list = await listReviewCaseComments(caseId, 2, new AbortController().signal);

    const url = new URL(requestUrl);
    expect([...url.searchParams.keys()]).toEqual(['page', 'pageSize']);
    expect(url.searchParams.get('page')).toBe('2');
    expect(url.searchParams.get('pageSize')).toBe('50');
    expect(list.totalCount).toBe('9223372036854775807');
    expect(list.totalCountValue).toBe(9223372036854775807n);
    expect(list.items[0]?.text).toBe('kept');
  });

  it('keeps items when the count disagrees and preserves text', () => {
    const list = readPage(pageBody([
      comment({ text: 'line\n<b>bold</b>\uFEFF', actorId: '\uFEFFactor' }),
    ], '0'));

    expect(list.totalCount).toBe('0');
    expect(list.items).toHaveLength(1);
    expect(list.items[0]).toEqual({
      id: commentId,
      text: 'line\n<b>bold</b>\uFEFF',
      actorId: '\uFEFFactor',
      recordedAt: '2026-11-06T12:00:00Z',
    });
  });

  it('rejects a count outside int64, a mismatched page, and a damaged comment', () => {
    expect(() => readPage(pageBody([comment()], '9223372036854775808'))).toThrow(UnexpectedResponseError);
    expect(() => readPage(pageBody([comment()], '-1'))).toThrow(UnexpectedResponseError);
    expect(() => readPage(pageBody([comment()], '1', 2))).toThrow(UnexpectedResponseError);
    expect(() => readPage(pageBody([comment({ id: '00000000-0000-0000-0000-000000000000' })], '1'))).toThrow(UnexpectedResponseError);
    expect(() => readPage(pageBody([comment({ text: ' kept' })], '1'))).toThrow(UnexpectedResponseError);
    expect(() => readPage(pageBody([comment({ text: 'a'.repeat(501) })], '1'))).toThrow(UnexpectedResponseError);
    expect(() => readPage(pageBody([comment({ actorId: ' actor' })], '1'))).toThrow(UnexpectedResponseError);
    expect(() => readPage(pageBody([comment({ actorId: 'a'.repeat(257) })], '1'))).toThrow(UnexpectedResponseError);
    expect(() => readPage(pageBody([comment({ recordedAt: null })], '1'))).toThrow(UnexpectedResponseError);
  });

  it('accepts a real calendar date and rejects impossible dates and wide offsets', () => {
    for (const recordedAt of [
      '2024-02-29T23:59:59Z',
      '0001-01-01T00:00:00Z',
      '9999-12-31T23:59:59.9999999Z',
      '0001-01-01T14:00:00+14:00',
      '9999-12-31T09:59:59.9999999-14:00',
      '9999-12-31T00:00:00-14:00',
    ]) {
      expect(readPage(pageBody([comment({ recordedAt })], '1')).items[0]?.recordedAt).toBe(recordedAt);
    }

    for (const recordedAt of [
      '0000-01-01T00:00:00Z',
      '0001-01-01T00:00:00+14:00',
      '9999-12-31T23:59:59-14:00',
      '2023-02-29T00:00:00Z',
      '2026-02-31T00:00:00Z',
      '2026-11-06T24:00:00Z',
      '2026-11-06T12:00:00+14:01',
      '2026-11-06T12:00:00-14:01',
      '2026-11-06T12:00:00+15:00',
      '2026-11-06T12:00:00+23:59',
    ]) {
      expect(() => readPage(pageBody([comment({ recordedAt })], '1'))).toThrow(UnexpectedResponseError);
    }
  });
});

function readPage(body: string) {
  return mapReviewCaseCommentList(parse(body), 1, 50);
}

function pageBody(items: Record<string, unknown>[], totalCount: string, page = 1): string {
  return `{"items":${JSON.stringify(items)},"page":${page},"pageSize":50,"totalCount":${totalCount}}`;
}

function comment(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    id: commentId,
    text: 'kept',
    actorId: 'original-actor',
    recordedAt: '2026-11-06T12:00:00Z',
    ...overrides,
  };
}
