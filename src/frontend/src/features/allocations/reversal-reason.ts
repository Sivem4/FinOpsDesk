const maximumReasonLength = 500;

export type ReversalReasonDecision =
  | { ok: true; reason: string }
  | { ok: false; message: string };

export function normalizeReversalReason(value: string): ReversalReasonDecision {
  const reason = trimLikeDotNet(value);

  if (reason.length === 0) {
    return { ok: false, message: 'Enter a reversal reason.' };
  }

  if (reason.length > maximumReasonLength) {
    return { ok: false, message: 'A reversal reason can contain at most 500 characters.' };
  }

  return { ok: true, reason };
}

export function trimLikeDotNet(value: string): string {
  let start = 0;
  let end = value.length;

  while (start < end && isDotNetWhiteSpace(value.charCodeAt(start))) {
    start += 1;
  }

  while (end > start && isDotNetWhiteSpace(value.charCodeAt(end - 1))) {
    end -= 1;
  }

  return value.slice(start, end);
}

function isDotNetWhiteSpace(code: number): boolean {
  if (code === 0x09 || code === 0x0a || code === 0x0b || code === 0x0c || code === 0x0d || code === 0x85) {
    return true;
  }

  if (code > 0xffff) {
    return false;
  }

  return /^\p{Zs}$|^\p{Zl}$|^\p{Zp}$/u.test(String.fromCharCode(code));
}
