import { File as NodeFile } from 'node:buffer';
import { FormData as NodeFormData, Request as UndiciRequest } from 'undici';

function copyBytes(bytes: Uint8Array): ArrayBuffer {
  const copy = new ArrayBuffer(bytes.byteLength);
  new Uint8Array(copy).set(bytes);
  return copy;
}

const JsdomFile = globalThis.File;
const JsdomFormData = globalThis.FormData;
const originalRequestFormData = Request.prototype.formData;

interface FilePart {
  key: string;
  bytes: ArrayBuffer;
  name: string;
  type: string;
  lastModified: number;
}

interface TextPart {
  key: string;
  text: string;
}

// jsdom's File is not the File implementation undici uses to serialize multipart
// bodies on Node 24. Without this adapter, reading an uploaded file in MSW fails
// with a content-length mismatch and the original bytes are dropped. The
// application still submits a FormData body and does not set Content-Type; this
// adapter only reproduces the multipart header the browser would add.
export function installMultipartFormDataCompat(): void {
  const currentFetch = globalThis.fetch.bind(globalThis);

  globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
    if (init?.body instanceof JsdomFormData) {
      const serialized = await serializeFormData(init.body);
      const headers = new Headers(init.headers);

      if (!headers.has('content-type')) {
        headers.set('content-type', serialized.contentType);
      }

      return currentFetch(input, { ...init, headers, body: serialized.bytes });
    }

    return currentFetch(input, init);
  }) as typeof fetch;

  Request.prototype.formData = async function formData() {
    const previousFile = globalThis.File;
    globalThis.File = NodeFile as unknown as typeof File;

    try {
      const parsed = await originalRequestFormData.call(this);
      const result = new JsdomFormData();

      for (const [key, value] of parsed.entries()) {
        if (typeof value === 'string') {
          result.append(key, value);
          continue;
        }

        const bytes = new Uint8Array(await value.arrayBuffer());
        result.append(
          key,
          new JsdomFile([bytes], value.name, { type: value.type, lastModified: value.lastModified }),
        );
      }

      return result;
    } finally {
      globalThis.File = previousFile;
    }
  };
}

async function serializeFormData(form: FormData): Promise<{ bytes: ArrayBuffer; contentType: string }> {
  const parts: Array<TextPart | FilePart> = [];

  for (const [key, value] of form.entries()) {
    if (typeof value === 'string') {
      parts.push({ key, text: value });
      continue;
    }

    parts.push({
      key,
      bytes: copyBytes(new Uint8Array(await value.arrayBuffer())),
      name: value.name,
      type: value.type,
      lastModified: value.lastModified,
    });
  }

  const previousFile = globalThis.File;
  globalThis.File = NodeFile as unknown as typeof File;

  try {
    const nodeForm = new NodeFormData();

    for (const part of parts) {
      if ('text' in part) {
        nodeForm.append(part.key, part.text);
      } else {
        nodeForm.append(
          part.key,
          new NodeFile([new Uint8Array(part.bytes)], part.name, {
            type: part.type,
            lastModified: part.lastModified,
          }),
        );
      }
    }

    const preview = new UndiciRequest('http://localhost/multipart', { method: 'POST', body: nodeForm });
    const contentType = preview.headers.get('content-type');

    if (contentType === null) {
      throw new Error('Multipart content type was not generated.');
    }

    return { bytes: copyBytes(new Uint8Array(await preview.arrayBuffer())), contentType };
  } finally {
    globalThis.File = previousFile;
  }
}
