export function readUntrustedExpiry(token: string): number | null {
  const parts = token.split('.');

  if (parts.length !== 3 || parts.some((part) => part.length === 0)) {
    return null;
  }

  try {
    const payload = JSON.parse(decodeBase64Url(parts[1])) as unknown;

    if (!isRecord(payload) || typeof payload.exp !== 'number' || !Number.isSafeInteger(payload.exp)) {
      return null;
    }

    return payload.exp;
  } catch {
    return null;
  }
}

function decodeBase64Url(value: string): string {
  const base64 = value.replace(/-/g, '+').replace(/_/g, '/');
  const padded = base64.padEnd(base64.length + ((4 - (base64.length % 4)) % 4), '=');
  const binary = atob(padded);
  const bytes = Uint8Array.from(binary, (char) => char.charCodeAt(0));
  return new TextDecoder().decode(bytes);
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null;
}
