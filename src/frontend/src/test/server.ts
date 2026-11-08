import { http, HttpResponse } from 'msw';
import { setupServer } from 'msw/node';

export const server = setupServer(
  http.get('/api/allocation-disputes/:disputeId/resolution', ({ params }) => HttpResponse.json({
    disputeId: params.disputeId,
    decision: null,
  })),
  http.get('/api/review-cases/:id/assignment', ({ params }) => HttpResponse.json({
    reviewCaseId: params.id,
    assignee: null,
    concurrencyToken: 'AAAAAAAAAAA=',
  })),
);
