export function serializeReviewCaseAssignmentBody(concurrencyToken: string): string {
  return JSON.stringify({ concurrencyToken });
}

export function serializeReviewCaseDelegationBody(concurrencyToken: string, recipientActorId: string): string {
  return JSON.stringify({ concurrencyToken, recipientActorId });
}
