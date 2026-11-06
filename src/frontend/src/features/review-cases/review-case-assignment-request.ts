export function serializeReviewCaseAssignmentBody(concurrencyToken: string): string {
  return JSON.stringify({ concurrencyToken });
}
