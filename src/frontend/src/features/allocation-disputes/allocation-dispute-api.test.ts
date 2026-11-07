import { parse } from 'lossless-json';
import { describe, expect, it } from 'vitest';

import { UnexpectedResponseError } from '../../shared/api/errors';
import {
  allocationDisputeLastPage,
  allocationDisputeNextEnabled,
  mapAllocationDispute,
  mapAllocationDisputeList,
  mapRegisteredAllocationDispute,
  readAllocationDisputeAddress,
} from './allocation-dispute-api';

const allocationId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';
const otherAllocationId = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb';
const disputeId = 'cccccccc-cccc-4ccc-8ccc-cccccccccccc';
const otherDisputeId = 'dddddddd-dddd-4ddd-8ddd-dddddddddddd';
const aboveSafeInteger = '9007199254740993';

describe('allocation dispute response parser', () => {
  it('accepts both actor sources, a large count, and the original timestamp text', () => {
    const recordedAt = '2026-11-07T09:00:00.1+00:00';
    const list = mapAllocationDisputeList(read(listBody([
      dispute({ actorSource: 'developmentJwt', recordedAt }),
      dispute({
        disputeId: otherDisputeId,
        allocationId: otherAllocationId,
        actorSource: 'productionCookie',
        statement: 'second',
      }),
    ], aboveSafeInteger)), 1, null);

    expect(list.totalCount).toBe(aboveSafeInteger);
    expect(list.totalCountValue).toBe(9007199254740993n);
    expect(list.items[0]).toEqual({
      disputeId,
      allocationId,
      statement: 'counterparty challenge',
      actorSource: 'developmentJwt',
      actorId: 'e2e-operator',
      recordedAt,
    });
    expect(list.items[1]?.actorSource).toBe('productionCookie');
    expect(mapAllocationDispute(read(disputeBody({ actorSource: 'productionCookie' })), disputeId).actorSource)
      .toBe('productionCookie');
  });

  it('keeps U+FEFF at both ends and rejects .NET whitespace on an edge', () => {
    const statement = `\uFEFFkept\uFEFF`;
    const actorId = `\uFEFFoperator\uFEFF`;
    const list = mapAllocationDisputeList(read(listBody([dispute({ statement, actorId })], '1')), 1, null);

    expect(list.items[0]?.statement).toBe(statement);
    expect(list.items[0]?.actorId).toBe(actorId);
    expect(() => mapAllocationDisputeList(read(listBody([dispute({ statement: ' kept' })], '1')), 1, null))
      .toThrow(UnexpectedResponseError);
    expect(() => mapAllocationDisputeList(read(listBody([dispute({ actorId: 'operator ' })], '1')), 1, null))
      .toThrow(UnexpectedResponseError);
    expect(() => mapAllocationDisputeList(read(listBody([dispute({ statement: 'a'.repeat(501) })], '1')), 1, null))
      .toThrow(UnexpectedResponseError);
    expect(() => mapAllocationDisputeList(read(listBody([dispute({ actorId: 'a'.repeat(257) })], '1')), 1, null))
      .toThrow(UnexpectedResponseError);
    expect(mapAllocationDisputeList(read(listBody([
      dispute({ statement: 'a'.repeat(500), actorId: 'a'.repeat(256) }),
    ], '1')), 1, null).items).toHaveLength(1);
  });

  it('accepts the DateTimeOffset range and rejects a timestamp outside it', () => {
    for (const recordedAt of [
      '2024-02-29T23:59:59Z',
      '0001-01-01T00:00:00Z',
      '9999-12-31T23:59:59.9999999Z',
      '0001-01-01T14:00:00+14:00',
      '9999-12-31T09:59:59.9999999-14:00',
    ]) {
      expect(mapAllocationDispute(read(disputeBody({ recordedAt })), disputeId).recordedAt).toBe(recordedAt);
    }

    for (const recordedAt of [
      '0001-01-01T00:00:00+14:00',
      '9999-12-31T23:59:59-14:00',
      '2026-11-06T12:00:00+14:01',
      '2026-11-06T12:00:00.12345678Z',
      '2023-02-29T00:00:00Z',
    ]) {
      expect(() => mapAllocationDispute(read(disputeBody({ recordedAt })), disputeId)).toThrow(UnexpectedResponseError);
    }
  });

  it('echoes the requested page and page size and rejects a closed-shape violation', () => {
    expect(mapAllocationDisputeList(
      read(listBody([dispute()], '1', allocationDisputeLastPage)),
      allocationDisputeLastPage,
      null,
    ).page).toBe(allocationDisputeLastPage);
    expect(() => mapAllocationDisputeList(read(listBody([dispute()], '1', allocationDisputeLastPage + 1)), 1, null))
      .toThrow(UnexpectedResponseError);
    expect(() => mapAllocationDisputeList(read(listBody([dispute()], '1', 1, 49)), 1, null))
      .toThrow(UnexpectedResponseError);
    expect(() => mapAllocationDisputeList(read(listBody([dispute()], '9223372036854775808')), 1, null))
      .toThrow(UnexpectedResponseError);
    expect(mapAllocationDisputeList(read(listBody([dispute()], '9223372036854775807')), 1, null).totalCount)
      .toBe('9223372036854775807');

    const extra = JSON.parse(listBody([dispute()], '1')) as Record<string, unknown>;
    extra.organizationId = allocationId;
    expect(() => mapAllocationDisputeList(read(JSON.stringify(extra)), 1, null)).toThrow(UnexpectedResponseError);

    const detail = JSON.parse(disputeBody()) as Record<string, unknown>;
    detail.idempotencyKey = disputeId;
    expect(() => mapAllocationDispute(read(JSON.stringify(detail)), disputeId)).toThrow(UnexpectedResponseError);
    detail.status = 'Confirmed';
    delete detail.idempotencyKey;
    expect(() => mapAllocationDispute(read(JSON.stringify(detail)), disputeId)).toThrow(UnexpectedResponseError);
  });

  it('binds the detail id and a filtered allocation id by GUID value', () => {
    const upperDispute = disputeId.toUpperCase();
    const upperAllocation = allocationId.toUpperCase();
    expect(mapAllocationDispute(read(disputeBody({ disputeId: upperDispute })), disputeId).disputeId).toBe(upperDispute);
    expect(mapAllocationDisputeList(
      read(listBody([dispute({ allocationId: upperAllocation })], '1')),
      1,
      allocationId,
    ).items[0]?.allocationId).toBe(upperAllocation);
    expect(mapAllocationDisputeList(read(listBody([
      dispute(),
      dispute({ disputeId: otherDisputeId, allocationId: otherAllocationId }),
    ], '2')), 1, null).items).toHaveLength(2);

    expect(() => mapAllocationDispute(read(disputeBody({ disputeId: otherDisputeId })), disputeId))
      .toThrow(UnexpectedResponseError);
    expect(mapRegisteredAllocationDispute(read(disputeBody({ disputeId: otherDisputeId, actorId: 'historical-operator' })), allocationId, 'counterparty challenge'))
      .toMatchObject({ disputeId: otherDisputeId, actorId: 'historical-operator' });
    expect(() => mapRegisteredAllocationDispute(read(disputeBody({ disputeId: '' })), allocationId, 'counterparty challenge'))
      .toThrow(UnexpectedResponseError);
    expect(() => mapRegisteredAllocationDispute(read(disputeBody({ allocationId: otherAllocationId })), allocationId, 'counterparty challenge'))
      .toThrow(UnexpectedResponseError);
    expect(() => mapRegisteredAllocationDispute(read(disputeBody({ statement: 'other' })), allocationId, 'counterparty challenge'))
      .toThrow(UnexpectedResponseError);
    expect(() => mapAllocationDisputeList(
      read(listBody([dispute({ allocationId: otherAllocationId })], '1')),
      1,
      allocationId,
    )).toThrow(UnexpectedResponseError);
  });

  it('limits the next page by the Int32 offset and does not treat a divergent count as corrupt', () => {
    expect(allocationDisputeNextEnabled(1, 50, 50n)).toBe(true);
    expect(allocationDisputeNextEnabled(1, 1, 51n)).toBe(true);
    expect(allocationDisputeNextEnabled(1, 1, 50n)).toBe(false);
    expect(allocationDisputeNextEnabled(allocationDisputeLastPage, 50, 9223372036854775807n)).toBe(false);
    expect(allocationDisputeNextEnabled(allocationDisputeLastPage - 1, 50, 0n)).toBe(true);
  });
});

describe('allocation dispute address', () => {
  it('defaults the page and ignores an unknown name', () => {
    const address = readAllocationDisputeAddress(new URLSearchParams('sort=recordedAt'));
    expect(address).toMatchObject({
      listValid: true,
      page: 1,
      allocationId: null,
      selectedState: 'absent',
      selectedValues: [],
    });
  });

  it('accepts the last page and rejects the following page, duplicates, and a bad allocation id', () => {
    expect(readAllocationDisputeAddress(new URLSearchParams(`page=${allocationDisputeLastPage}`)).listValid).toBe(true);
    expect(readAllocationDisputeAddress(new URLSearchParams(`page=${allocationDisputeLastPage + 1}`)).listValid)
      .toBe(false);
    expect(readAllocationDisputeAddress(new URLSearchParams('page=0')).listValid).toBe(false);
    expect(readAllocationDisputeAddress(new URLSearchParams('page=01')).listValid).toBe(false);
    expect(readAllocationDisputeAddress(new URLSearchParams('page=1&page=2')).listValid).toBe(false);
    expect(readAllocationDisputeAddress(new URLSearchParams(`allocationId=${allocationId}&allocationId=${allocationId}`)).listValid)
      .toBe(false);
    expect(readAllocationDisputeAddress(new URLSearchParams('allocationId=')).listValid).toBe(false);
    expect(readAllocationDisputeAddress(new URLSearchParams('allocationId=00000000-0000-0000-0000-000000000000')).listValid)
      .toBe(false);
    expect(readAllocationDisputeAddress(new URLSearchParams('allocationId=not-a-guid')).listValid).toBe(false);
  });

  it('keeps the list when only the selection is invalid', () => {
    const address = readAllocationDisputeAddress(new URLSearchParams(
      `page=2&allocationId=${allocationId}&selected=&selected=${disputeId}`,
    ));
    expect(address.listValid).toBe(true);
    expect(address.page).toBe(2);
    expect(address.allocationId).toBe(allocationId);
    expect(address.selectedState).toBe('invalid');
    expect(address.selectedValues).toEqual(['', disputeId]);
  });
});

function read(body: string): unknown {
  return parse(body);
}

function listBody(
  items: Record<string, unknown>[],
  totalCount: string,
  page = 1,
  pageSize = 50,
): string {
  return `{"items":${JSON.stringify(items)},"page":${page},"pageSize":${pageSize},"totalCount":${totalCount}}`;
}

function disputeBody(overrides: Record<string, unknown> = {}): string {
  return JSON.stringify(dispute(overrides));
}

function dispute(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    disputeId,
    allocationId,
    statement: 'counterparty challenge',
    actorSource: 'developmentJwt',
    actorId: 'e2e-operator',
    recordedAt: '2026-11-07T09:00:00Z',
    ...overrides,
  };
}
