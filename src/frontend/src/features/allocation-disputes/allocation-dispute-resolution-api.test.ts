import { parse } from 'lossless-json';
import { describe, expect, it } from 'vitest';

import { UnexpectedResponseError } from '../../shared/api/errors';
import {
  mapAllocationDispute,
  mapAllocationDisputeResolutionDecision,
  mapAllocationDisputeResolutionState,
  normalizeAllocationDisputeResolutionReason,
} from './allocation-dispute-api';

const disputeId = 'cccccccc-cccc-4ccc-8ccc-cccccccccccc';
const otherDisputeId = 'dddddddd-dddd-4ddd-8ddd-dddddddddddd';

describe('allocation dispute resolution parser', () => {
  it('accepts a closed null decision and keeps a correct server value unchanged', () => {
    const decidedAt = '2026-11-07T09:00:00.1+00:00';
    const reason = `\uFEFFkept\uFEFF`;
    const actorId = `\uFEFFoperator\uFEFF`;
    const state = mapAllocationDisputeResolutionState(read({
      disputeId: disputeId.toUpperCase(),
      decision: null,
    }), disputeId);

    expect(state).toEqual({ disputeId: disputeId.toUpperCase(), decision: null });
    expect(mapAllocationDisputeResolutionDecision(read(decision({
      reason,
      actorId,
      decidedAt,
      actorSource: 'productionCookie',
    })), disputeId)).toEqual({
      disputeId,
      outcome: 'upheld',
      reason,
      actorSource: 'productionCookie',
      actorId,
      decidedAt,
    });
    expect(normalizeAllocationDisputeResolutionReason(` \t${reason}\n`)).toBe(reason);
  });

  it('accepts the DateTimeOffset range and rejects a timestamp outside it', () => {
    for (const decidedAt of [
      '2024-02-29T23:59:59Z',
      '0001-01-01T00:00:00Z',
      '9999-12-31T23:59:59.9999999Z',
      '0001-01-01T14:00:00+14:00',
      '9999-12-31T09:59:59.9999999-14:00',
    ]) {
      expect(mapAllocationDisputeResolutionDecision(read(decision({ decidedAt })), disputeId).decidedAt).toBe(decidedAt);
    }

    for (const decidedAt of [
      '0001-01-01T00:00:00+14:00',
      '9999-12-31T23:59:59-14:00',
      '2026-11-07T09:00:00+14:01',
      '2026-11-07T09:00:00.12345678Z',
      '2026-13-01T00:00:00Z',
    ]) {
      expect(() => mapAllocationDisputeResolutionDecision(read(decision({ decidedAt })), disputeId))
        .toThrow(UnexpectedResponseError);
    }
  });

  it('rejects an open shape, a mismatched id, and a reason the server did not normalize', () => {
    expect(() => mapAllocationDisputeResolutionState(read({ disputeId, decision: null, extra: true }), disputeId))
      .toThrow(UnexpectedResponseError);
    expect(() => mapAllocationDisputeResolutionState(read({ disputeId }), disputeId)).toThrow(UnexpectedResponseError);
    expect(() => mapAllocationDisputeResolutionState(read({
      disputeId: otherDisputeId,
      decision: null,
    }), disputeId)).toThrow(UnexpectedResponseError);
    expect(() => mapAllocationDisputeResolutionDecision(read(decision({ disputeId: otherDisputeId })), disputeId))
      .toThrow(UnexpectedResponseError);
    expect(() => mapAllocationDisputeResolutionState(read({
      disputeId,
      decision: decision({ disputeId: otherDisputeId }),
    }), disputeId)).toThrow(UnexpectedResponseError);
    expect(() => mapAllocationDisputeResolutionDecision(read(decision({ outcome: 'upheld ' })), disputeId))
      .toThrow(UnexpectedResponseError);
    expect(() => mapAllocationDisputeResolutionDecision(read(decision({ reason: ' kept' })), disputeId))
      .toThrow(UnexpectedResponseError);
    expect(() => mapAllocationDisputeResolutionDecision(read(decision({ reason: '' })), disputeId))
      .toThrow(UnexpectedResponseError);
    expect(() => mapAllocationDisputeResolutionDecision(read(decision({ reason: 'a'.repeat(501) })), disputeId))
      .toThrow(UnexpectedResponseError);
    expect(() => mapAllocationDisputeResolutionDecision(read({ ...decision(), idempotencyKey: 'extra' }), disputeId))
      .toThrow(UnexpectedResponseError);
    expect(mapAllocationDispute(read({
      disputeId,
      allocationId: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
      statement: 'counterparty challenge',
      actorSource: 'developmentJwt',
      actorId: 'operator-1',
      recordedAt: '2026-11-07T09:00:00Z',
    }), disputeId).statement).toBe('counterparty challenge');
  });
});

function decision(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    disputeId,
    outcome: 'upheld',
    reason: 'counterparty challenge',
    actorSource: 'developmentJwt',
    actorId: 'operator-1',
    decidedAt: '2026-11-07T10:00:00Z',
    ...overrides,
  };
}

function read(value: unknown): unknown {
  return parse(JSON.stringify(value));
}
