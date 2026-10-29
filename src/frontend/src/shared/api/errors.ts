export class ApiError extends Error {
  readonly status: number;

  readonly code: string | null;

  readonly title: string;

  readonly detail: string;

  constructor(status: number, code: string | null, title: string, detail: string) {
    super(title);
    this.name = 'ApiError';
    this.status = status;
    this.code = code;
    this.title = title;
    this.detail = detail;
  }
}

export class NetworkError extends Error {
  constructor() {
    super('The API could not be reached.');
    this.name = 'NetworkError';
  }
}

export class StaleSessionError extends Error {
  constructor() {
    super('The request belonged to a session that is no longer current.');
    this.name = 'StaleSessionError';
  }
}

export class UnexpectedResponseError extends Error {
  constructor() {
    super('The API returned a response the invoice register cannot read.');
    this.name = 'UnexpectedResponseError';
  }
}

export function isAbortError(error: unknown): boolean {
  return error instanceof DOMException && error.name === 'AbortError';
}
