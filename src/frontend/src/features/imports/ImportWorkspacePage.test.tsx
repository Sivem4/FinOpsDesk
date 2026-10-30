import { onlineManager, QueryClientProvider } from '@tanstack/react-query';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { http, HttpResponse } from 'msw';
import { MemoryRouter } from 'react-router';
import { afterEach, describe, expect, it } from 'vitest';

import { StaleSessionError } from '../../shared/api/errors';
import { queryClient } from '../../shared/api/query-client';
import { sessionStore } from '../../shared/api/session-store';
import { server } from '../../test/server';
import { postImport } from './import-api';
import { maxImportFileBytes } from './import-form';
import { ImportWorkspacePage } from './ImportWorkspacePage';

const batchId = '11111111-1111-1111-1111-111111111111';
const fileHash = 'a'.repeat(64);

afterEach(() => {
  onlineManager.setOnline(true);
});

describe('csv import workspace', () => {
  it('requires a source system, a file, and a file of at most 2 MiB', async () => {
    const user = userEvent.setup();
    renderWorkspace();

    await user.click(screen.getByRole('button', { name: 'Import file' }));
    expect(await screen.findByText('Enter where this file came from.')).toBeInTheDocument();
    expect(screen.getByText('Choose a CSV file to continue.')).toBeInTheDocument();

    await user.click(screen.getByLabelText('File source'));
    await user.paste('   ');
    await user.click(screen.getByRole('button', { name: 'Import file' }));
    expect(screen.getByText('Enter where this file came from.')).toBeInTheDocument();

    await user.clear(screen.getByLabelText('File source'));
    await user.click(screen.getByLabelText('File source'));
    await user.paste('a'.repeat(65));
    await user.click(screen.getByRole('button', { name: 'Import file' }));
    expect(
      screen.getByText('Use a source name of 64 characters or fewer.'),
    ).toBeInTheDocument();

    await user.clear(screen.getByLabelText('File source'));
    await user.type(screen.getByLabelText('File source'), 'Demo');
    await user.upload(
      screen.getByLabelText('CSV file'),
      new File([new Uint8Array(maxImportFileBytes + 1)], 'large.csv'),
    );
    await user.click(screen.getByRole('button', { name: 'Import file' }));
    expect(screen.getByText('Choose a file smaller than 2 MiB.')).toBeInTheDocument();
  });

  it('shows the headers for the selected import type', async () => {
    const user = userEvent.setup();
    renderWorkspace();

    expect(screen.getByText('Add invoices or bank transactions from a CSV file.')).toBeInTheDocument();
    expect(screen.getByLabelText('File source')).toHaveAccessibleDescription(/Where is this file from/);
    await user.hover(screen.getByRole('button', { name: 'How the file source is stored' }));
    expect(await screen.findByRole('tooltip')).toHaveTextContent(/stored in capitals/);
    await user.unhover(screen.getByRole('button', { name: 'How the file source is stored' }));
    expect(screen.getByLabelText('CSV file')).toHaveAccessibleDescription(/up to 2 MiB/);
    expect(screen.queryByText(/ExternalId/)).not.toBeInTheDocument();
    const help = screen.getByRole('button', { name: 'File requirements' });
    expect(help).toHaveAttribute('type', 'button');
    expect(help).toHaveAttribute('aria-expanded', 'false');
    help.focus();
    await user.keyboard('{Enter}');
    expect(help).toHaveAttribute('aria-expanded', 'true');
    expect(screen.getByRole('region', { name: 'Prepare your CSV' })).toHaveAttribute('id', help.getAttribute('aria-controls'));
    expect(screen.getByText(/ExternalId,InvoiceNumber,Amount,Currency/)).toBeInTheDocument();
    expect(screen.getByText(/Columns can be in any order/)).toBeInTheDocument();
    expect(screen.queryByText(/BookingDate/)).not.toBeInTheDocument();

    await user.click(screen.getByRole('radio', { name: 'Bank transactions' }));

    expect(screen.getByText(/ExternalId,BookingDate,Amount,Currency,Reference/)).toBeInTheDocument();
    expect(screen.getByText(/Use dates like/)).toBeInTheDocument();
    await user.click(help);
    expect(help).toHaveAttribute('aria-expanded', 'false');
    expect(screen.queryByRole('region', { name: 'Prepare your CSV' })).not.toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('posts the original invoice file without setting the multipart content type', async () => {
    const user = userEvent.setup();
    const csv = 'ExternalId,InvoiceNumber,Amount,Currency\r\ndemo-100,INV-100,1250.50,PLN\r\n';
    let contentType: string | null = null;
    let method = '';
    let sourceSystem = '';
    let bytes = '';
    let fileName = '';
    server.use(
      http.post('/api/imports/invoices', async ({ request }) => {
        method = request.method;
        contentType = request.headers.get('content-type');
        const form = await request.formData();
        sourceSystem = String(form.get('sourceSystem'));
        const uploaded = form.get('file');
        if (uploaded instanceof File) {
          bytes = await uploaded.text();
          fileName = uploaded.name;
        }
        return importResult();
      }),
    );
    renderWorkspace();

    await submitFile(user, new File([csv], 'notes.txt', { type: 'text/csv' }), ' Demo ');

    expect(await screen.findByRole('heading', { name: 'Import result' })).toBeInTheDocument();
    await openTechnicalDetails(user);
    expect(screen.getByText(`Batch ID: ${batchId}`)).toBeVisible();
    expect(method).toBe('POST');
    expect(sourceSystem).toBe('Demo');
    expect(bytes).toBe(csv);
    expect(fileName).toBe('notes.txt');
    expect((contentType ?? '').toLowerCase().startsWith('multipart/form-data; boundary=')).toBe(true);
    expect(screen.getByText('All rows have been checked. Review the totals below.')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Review invoices' })).toHaveAttribute('href', '/invoices');
    expect(counterValue('Added')).toBe('1');
    expect(counterValue('Already in workspace')).toBe('0');
    expect(counterValue('Needs attention')).toBe('0');
    expect(counterValue('Rows in file')).toBe('1');
  });

  it('posts a bank transaction file to the bank endpoint', async () => {
    const user = userEvent.setup();
    const csv = 'ExternalId,BookingDate,Amount,Currency,Reference\r\nbank-1,2026-10-29,10.00,PLN,\r\n';
    let bytes = '';
    server.use(
      http.post('/api/imports/bank-transactions', async ({ request }) => {
        const form = await request.formData();
        const uploaded = form.get('file');
        bytes = uploaded instanceof File ? await uploaded.text() : '';
        expect(form.get('sourceSystem')).toBe('Bank');
        return importResult({
          importType: 'BankTransaction',
          sourceSystem: 'BANK',
          fileName: 'bank.csv',
        });
      }),
    );
    renderWorkspace();
    await user.click(screen.getByRole('radio', { name: 'Bank transactions' }));
    await submitFile(user, new File([csv], 'bank.csv'), 'Bank');

    expect(await screen.findByRole('heading', { name: 'Import result' })).toBeInTheDocument();
    await openTechnicalDetails(user);
    expect(screen.getByText('Import type: BankTransaction')).toBeVisible();
    expect(bytes).toBe(csv);
    expect(screen.queryByRole('link', { name: 'Review invoices' })).not.toBeInTheDocument();
  });

  it('disables submission while the upload is in progress', async () => {
    const user = userEvent.setup();
    let calls = 0;
    let release: () => void = () => {};
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    server.use(
      http.post('/api/imports/invoices', async () => {
        calls += 1;
        await gate;
        return importResult();
      }),
    );
    renderWorkspace();
    await submitFile(user, sampleFile());

    expect(await screen.findByRole('status')).toHaveTextContent('Import in progress');
    expect(screen.getByRole('button', { name: 'Import file' })).toBeDisabled();
    expect(calls).toBe(1);
    release();
    await openTechnicalDetails(user);
    expect(screen.getByText(`Batch ID: ${batchId}`)).toBeVisible();
  });

  it.each([
    ['Completed', 'All rows have been checked. Review the totals below.', 1, 0],
    ['CompletedWithErrors', 'Some rows were not added. The counts show what was added and what was already here.', 1, 1],
    ['Rejected', 'Fix the rows listed below, then import your file again.', 0, 1],
  ] as const)(
    'shows a stored %s outcome together with replay information',
    async (status, summary, importedRows, rejectedRows) => {
      const user = userEvent.setup();
      server.use(
        http.post('/api/imports/invoices', () =>
          importResult({
            replayed: true,
            status,
            importedRows,
            alreadyExistsRows: 0,
            rejectedRows,
            totalRows: importedRows + rejectedRows,
            rowErrors: rejectedRows === 0 ? [] : [rowError(1, 2)],
          }),
        ),
      );
      renderWorkspace();
      await submitFile(user, sampleFile());

      expect(await screen.findByText(summary)).toBeInTheDocument();
      expect(
        screen.getByText(/Here’s the earlier result. Nothing was added again/),
      ).toBeInTheDocument();
      expect(screen.getByText('You’ve already imported this file')).toBeInTheDocument();
      await openTechnicalDetails(user);
      expect(screen.getByText(`Status: ${status}`)).toBeVisible();
      if (importedRows > 0) {
        expect(screen.getByRole('link', { name: 'Review invoices' })).toHaveAttribute('href', '/invoices');
      } else {
        expect(screen.queryByRole('link', { name: 'Review invoices' })).not.toBeInTheDocument();
      }
    },
  );

  it('shows completed-with-errors counts, source lines, and the next error page', async () => {
    const user = userEvent.setup();
    const rowErrors = Array.from({ length: 26 }, (_value, index) =>
      rowError(index + 1, index === 0 ? 40 : 100 + index),
    );
    server.use(
      http.post('/api/imports/invoices', () =>
        importResult({
          status: 'CompletedWithErrors',
          totalRows: 27,
          importedRows: 1,
          alreadyExistsRows: 0,
          rejectedRows: 26,
          rowErrors,
        }),
      ),
    );
    renderWorkspace();
    await submitFile(user, sampleFile());

    expect(
      await screen.findByText('Some rows were not added. The counts show what was added and what was already here.'),
    ).toBeInTheDocument();
    expect(counterValue('Added')).toBe('1');
    expect(counterValue('Already in workspace')).toBe('0');
    expect(counterValue('Needs attention')).toBe('26');
    expect(screen.getByRole('heading', { name: '26 rows need attention' })).toBeVisible();
    expect(screen.getByText('Showing rows 1 to 25 of 26')).toBeVisible();
    expect(screen.getByText('Some rows need attention')).toBeInTheDocument();
    expect(counterValue('Rows in file')).toBe('27');
    expect(screen.getByRole('columnheader', { name: 'Row' })).toBeInTheDocument();
    expect(screen.getByRole('columnheader', { name: 'Line' })).toBeInTheDocument();
    expect(screen.getByRole('cell', { name: '40' })).toBeInTheDocument();
    expect(screen.getByRole('columnheader', { name: 'Field' })).toBeInTheDocument();
    expect(screen.getByRole('columnheader', { name: 'What to change' })).toBeInTheDocument();
    expect(screen.queryByRole('columnheader', { name: 'Error code' })).not.toBeInTheDocument();
    expect(screen.getAllByText('Amount must be a positive decimal(19,4) value without rounding.')).toHaveLength(25);
    expect(screen.getAllByText('reconciliation.import.invalid_amount')).toHaveLength(25);
    expect(screen.queryByText(/More information is available in Technical details/)).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Exact message for row 1' })).not.toBeInTheDocument();
    expect(screen.getByText(/Existing records won’t be added twice/)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Review invoices' })).toBeInTheDocument();
    expect(screen.queryByRole('columnheader', { name: 'Invoice number' })).not.toBeInTheDocument();
    await openTechnicalDetails(user);
    expect(screen.getByText('SHA-256: ' + fileHash)).toBeVisible();

    await user.click(screen.getByRole('button', { name: 'Next errors' }));
    expect(screen.getByText('Showing row 26 of 26')).toBeVisible();
    expect(screen.getByRole('cell', { name: '26' })).toBeInTheDocument();
    expect(screen.queryByRole('cell', { name: '40' })).not.toBeInTheDocument();
  });

  it('shows a persisted rejected batch returned with HTTP 201', async () => {
    const user = userEvent.setup();
    server.use(
      http.post('/api/imports/invoices', () =>
        importResult({
          status: 'Rejected',
          totalRows: 1,
          importedRows: 0,
          alreadyExistsRows: 0,
          rejectedRows: 1,
          rowErrors: [rowError(1, 2)],
        }),
      ),
    );
    renderWorkspace();
    await submitFile(user, sampleFile());

    expect(await screen.findByText('Fix the rows listed below, then import your file again.')).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: '1 row needs attention' })).toBeVisible();
    expect(screen.queryByText(/^Showing /)).not.toBeInTheDocument();
    expect(screen.queryByText(/Rows to fix ·/)).not.toBeInTheDocument();
    expect(
      screen.queryByText('All rows have been checked. Review the totals below.'),
    ).not.toBeInTheDocument();
    expect(screen.queryByText(/previously stored result/)).not.toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Review invoices' })).not.toBeInTheDocument();
    expect(screen.queryByText(`Batch ID: ${batchId}`)).not.toBeVisible();
    await openTechnicalDetails(user);
    expect(screen.getByText(`Batch ID: ${batchId}`)).toBeVisible();
    expect(screen.getByText('Status: Rejected')).toBeVisible();
  });

  it('shows a structural HTTP 400 without a batch', async () => {
    const user = userEvent.setup();
    server.use(
      http.post('/api/imports/invoices', () =>
        HttpResponse.json(
          {
            title: 'The CSV file is invalid.',
            detail: 'The CSV file is empty, malformed, or does not match the expected header.',
            code: 'reconciliation.import.invalid_file',
          },
          { status: 400 },
        ),
      ),
    );
    renderWorkspace();
    await submitFile(user, sampleFile());

    expect(await screen.findByText(/Nothing was imported/)).toBeInTheDocument();
    expect(screen.getByText(/The CSV file is empty, malformed, or does not match the expected header\./)).toBeInTheDocument();
    expect(screen.getByText('The CSV file is invalid.')).toBeInTheDocument();
    expect(screen.queryByText(`Batch ID: ${batchId}`)).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Technical details' })).not.toBeInTheDocument();
  });

  it('distinguishes unauthorized, forbidden, conflict, and file-too-large responses', async () => {
    const user = userEvent.setup();
    server.use(http.post('/api/imports/invoices', () => new HttpResponse(null, { status: 401 })));
    const unauthorized = renderWorkspace();
    await submitFile(user, sampleFile());
    expect(await screen.findByText('Your access has expired or is no longer valid. Update it to continue.')).toBeInTheDocument();
    unauthorized.view.unmount();

    server.use(http.post('/api/imports/invoices', () => new HttpResponse(null, { status: 403 })));
    const forbidden = renderWorkspace();
    await submitFile(user, sampleFile());
    expect(await screen.findByText('This sign-in isn’t connected to an organization.')).toBeInTheDocument();
    forbidden.view.unmount();

    server.use(
      http.post('/api/imports/invoices', () =>
        HttpResponse.json(
          {
            title: 'Import conflict was not resolved.',
            detail: 'The import could not be completed because a concurrent import kept changing the same records.',
            code: 'reconciliation.import.conflict_unresolved',
          },
          { status: 409 },
        ),
      ),
    );
    const conflict = renderWorkspace();
    await submitFile(user, sampleFile());
    const conflictAlert = await screen.findByRole('alert');
    expect(conflictAlert).toHaveTextContent('Please try again shortly');
    expect(conflictAlert).toHaveTextContent('The import was not saved.');
    expect(conflictAlert).toHaveTextContent(
      'The import could not be completed because a concurrent import kept changing the same records.',
    );
    expect(conflictAlert).toHaveTextContent('Import conflict was not resolved.');
    expect(screen.queryByText(/Another import is updating these records/)).not.toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Import result' })).not.toBeInTheDocument();
    conflict.view.unmount();

    server.use(
      http.post('/api/imports/invoices', () =>
        HttpResponse.json(
          {
            title: 'File is too large.',
            detail: 'The CSV file is larger than 2 MiB.',
            code: 'reconciliation.import.file_too_large',
          },
          { status: 413 },
        ),
      ),
    );
    renderWorkspace();
    await submitFile(user, sampleFile());
    expect(await screen.findByText(/Choose a smaller CSV file/)).toBeInTheDocument();
  });

  it('does not retry automatically and retries only from the unknown-outcome action', async () => {
    const user = userEvent.setup();
    let calls = 0;
    server.use(
      http.post('/api/imports/invoices', () => {
        calls += 1;
        if (calls === 1) {
          return HttpResponse.error();
        }

        return importResult({ replayed: true });
      }),
    );
    renderWorkspace();
    await submitFile(user, sampleFile());

    expect(await screen.findByText(/We don’t know whether this file was saved/)).toBeInTheDocument();
    expect(screen.getByText(/you’ll see the earlier result/)).toBeInTheDocument();
    expect(calls).toBe(1);

    await user.click(screen.getByRole('button', { name: 'Send the same file again' }));
    expect(await screen.findByText(/Nothing was added again/)).toBeInTheDocument();
    expect(calls).toBe(2);
  });

  it('retries the captured file and source system after the form changes', async () => {
    const user = userEvent.setup();
    const calls: Array<{ source: string; bytes: string; name: string }> = [];
    server.use(
      http.post('/api/imports/invoices', async ({ request }) => {
        const form = await request.formData();
        const uploaded = form.get('file');
        calls.push({
          source: String(form.get('sourceSystem')),
          bytes: uploaded instanceof File ? await uploaded.text() : '',
          name: uploaded instanceof File ? uploaded.name : '',
        });

        if (calls.length === 1) {
          return HttpResponse.error();
        }

        return importResult({ replayed: true, sourceSystem: 'SAP' });
      }),
    );
    renderWorkspace();
    await submitFile(user, new File(['file-a'], 'a.csv', { type: 'text/csv' }), 'SAP');
    expect(await screen.findByText(/We don’t know whether this file was saved/)).toBeInTheDocument();

    await user.upload(screen.getByLabelText('CSV file'), new File(['file-b'], 'b.csv', { type: 'text/csv' }));
    await user.clear(screen.getByLabelText('File source'));
    await user.click(screen.getByLabelText('File source'));
    await user.paste('OTHER');
    await user.click(screen.getByRole('button', { name: 'Send the same file again' }));

    expect(await screen.findByText(/Nothing was added again/)).toBeInTheDocument();
    expect(calls).toEqual([
      { source: 'SAP', bytes: 'file-a', name: 'a.csv' },
      { source: 'SAP', bytes: 'file-a', name: 'a.csv' },
    ]);
  });

  it('does not post an attempt captured for a session that has rotated', async () => {
    let calls = 0;
    server.use(
      http.post('/api/imports/invoices', () => {
        calls += 1;
        return importResult();
      }),
    );
    sessionStore.rotate('memory-token', Math.floor(Date.now() / 1000) + 3600, null);
    const capturedEpoch = sessionStore.getSnapshot().epoch;
    sessionStore.rotate('replacement-token', Math.floor(Date.now() / 1000) + 3600, null);

    await expect(
      postImport(
        'invoices',
        'SAP',
        new File(['file-a'], 'a.csv', { type: 'text/csv' }),
        new AbortController().signal,
        capturedEpoch,
      ),
    ).rejects.toBeInstanceOf(StaleSessionError);
    expect(calls).toBe(0);
  });

  it('does not queue an offline import for submission when connectivity returns', async () => {
    const user = userEvent.setup();
    let calls = 0;
    server.use(
      http.post('/api/imports/invoices', () => {
        calls += 1;
        return importResult();
      }),
    );
    onlineManager.setOnline(false);
    renderWorkspace();
    await submitFile(user, sampleFile(), 'SAP');

    expect(await screen.findByRole('heading', { name: 'Import result' })).toBeInTheDocument();
    expect(calls).toBe(1);

    onlineManager.setOnline(true);
    await new Promise((resolve) => setTimeout(resolve, 25));
    expect(calls).toBe(1);
    await openTechnicalDetails(user);
    expect(screen.getByText('Source system: DEMO')).toBeVisible();
  });

  it('does not treat an unreadable HTTP 201 body as a completed import', async () => {
    const user = userEvent.setup();
    let calls = 0;
    server.use(
      http.post('/api/imports/invoices', () => {
        calls += 1;

        if (calls === 1) {
          return new HttpResponse('not-json', {
            status: 201,
            headers: { 'Content-Type': 'application/json' },
          });
        }

        return importResult({ replayed: true });
      }),
    );
    renderWorkspace();
    await submitFile(user, sampleFile(), 'SAP');

    expect(await screen.findByText(/We don’t know whether this file was saved/)).toBeInTheDocument();
    expect(
      screen.queryByText('All rows have been checked. Review the totals below.'),
    ).not.toBeInTheDocument();
    expect(screen.queryByText(`Batch ID: ${batchId}`)).not.toBeInTheDocument();
    expect(calls).toBe(1);

    await user.click(screen.getByRole('button', { name: 'Send the same file again' }));
    expect(await screen.findByText(/Nothing was added again/)).toBeInTheDocument();
    expect(calls).toBe(2);
  });

  it('shows the lowercase SHA-256 digest stored by the API and accepts uppercase hex', async () => {
    const user = userEvent.setup();
    const lowercaseDigest = '0123456789abcdef'.repeat(4);
    const uppercaseDigest = lowercaseDigest.toUpperCase();
    let calls = 0;
    server.use(
      http.post('/api/imports/invoices', () => {
        calls += 1;
        return importResult({
          fileHash: calls === 1 ? lowercaseDigest : uppercaseDigest,
          replayed: calls > 1,
        });
      }),
    );
    renderWorkspace();
    await submitFile(user, sampleFile());

    await openTechnicalDetails(user);
    expect(screen.getByText(`SHA-256: ${lowercaseDigest}`)).toBeVisible();
    expect(screen.getByText('Created: 2026-10-29T12:00:00+00:00')).toBeVisible();
    expect(screen.queryByText(/We don’t know whether this file was saved/)).not.toBeInTheDocument();

    await submitFile(user, sampleFile());
    await openTechnicalDetails(user);
    expect(screen.getByText(`SHA-256: ${uppercaseDigest}`)).toBeVisible();
    expect(screen.queryByText(/We don’t know whether this file was saved/)).not.toBeInTheDocument();
  });

  it('does not show a result that arrives after the session is replaced', async () => {
    const user = userEvent.setup();
    let release: () => void = () => {};
    const gate = new Promise<void>((resolve) => {
      release = resolve;
    });
    server.use(
      http.post('/api/imports/invoices', async () => {
        await gate;
        return importResult();
      }),
    );
    renderWorkspace();
    await submitFile(user, sampleFile());
    expect(await screen.findByRole('status')).toHaveTextContent('Import in progress');

    sessionStore.rotate('replacement-token', Math.floor(Date.now() / 1000) + 3600, null);
    release();

    expect(await screen.findByText(/Your session changed/)).toBeInTheDocument();
    expect(screen.queryByText(`Batch ID: ${batchId}`)).not.toBeInTheDocument();
    expect(screen.queryByText(fileHash)).not.toBeInTheDocument();
  });

  it('aborts the upload when the page is left', async () => {
    const user = userEvent.setup();
    let aborted = false;
    server.use(
      http.post('/api/imports/invoices', async ({ request }) => {
        aborted = request.signal.aborted;
        await new Promise<void>((resolve) => {
          if (request.signal.aborted) {
            resolve();
            return;
          }

          request.signal.addEventListener('abort', () => {
            aborted = true;
            resolve();
          });
        });
        return new HttpResponse(null, { status: 499 });
      }),
    );
    const mounted = renderWorkspace();
    await submitFile(user, sampleFile());
    expect(await screen.findByRole('status')).toHaveTextContent('Import in progress');
    mounted.view.unmount();

    await waitFor(() => {
      expect(aborted).toBe(true);
    });
  });

  it('invalidates invoice queries only after a confirmed invoice response', async () => {
    const user = userEvent.setup();
    server.use(http.post('/api/imports/invoices', () => importResult()));
    const created = renderWorkspace();
    const createdKey = ['invoices', created.epoch, 1, 50] as const;
    await submitFile(user, sampleFile());
    await openTechnicalDetails(user);
    expect(screen.getByText(`Batch ID: ${batchId}`)).toBeVisible();
    await waitFor(() => {
      expect(queryClient.getQueryState(createdKey)?.isInvalidated).toBe(true);
    });
    created.view.unmount();

    server.use(
      http.post('/api/imports/invoices', () => importResult({ replayed: true })),
    );
    const replay = renderWorkspace();
    const replayKey = ['invoices', replay.epoch, 1, 50] as const;
    await submitFile(user, sampleFile());
    await screen.findByText(/Nothing was added again/);
    await waitFor(() => {
      expect(queryClient.getQueryState(replayKey)?.isInvalidated).toBe(true);
    });
    replay.view.unmount();

    server.use(
      http.post('/api/imports/bank-transactions', () =>
        importResult({ importType: 'BankTransaction' }),
      ),
    );
    const bank = renderWorkspace();
    const bankKey = ['invoices', bank.epoch, 1, 50] as const;
    await user.click(screen.getByRole('radio', { name: 'Bank transactions' }));
    await submitFile(user, sampleFile(), 'Bank');
    await openTechnicalDetails(user);
    expect(screen.getByText('Import type: BankTransaction')).toBeVisible();
    expect(queryClient.getQueryState(bankKey)?.isInvalidated).toBe(false);
    bank.view.unmount();

    server.use(http.post('/api/imports/invoices', () => HttpResponse.error()));
    const unknown = renderWorkspace();
    const unknownKey = ['invoices', unknown.epoch, 1, 50] as const;
    await submitFile(user, sampleFile());
    await screen.findByText(/We don’t know whether this file was saved/);
    expect(queryClient.getQueryState(unknownKey)?.isInvalidated).toBe(false);
  });

  it('invalidates matching queries after confirmed invoice and bank imports', async () => {
    const user = userEvent.setup();

    server.use(http.post('/api/imports/invoices', () => importResult()));
    const created = renderWorkspace();
    seedMatches(created.epoch);
    await submitFile(user, sampleFile());
    await screen.findByText('Import complete');
    await waitFor(() => {
      expect(queryClient.getQueryState(['matches', created.epoch, 'exact'])?.isInvalidated).toBe(true);
      expect(queryClient.getQueryState(['matches', created.epoch, 'smart'])?.isInvalidated).toBe(true);
    });
    created.view.unmount();

    server.use(http.post('/api/imports/invoices', () => importResult({ replayed: true })));
    const replay = renderWorkspace();
    seedMatches(replay.epoch);
    await submitFile(user, sampleFile());
    await screen.findByText(/Nothing was added again/);
    await waitFor(() => {
      expect(queryClient.getQueryState(['matches', replay.epoch, 'exact'])?.isInvalidated).toBe(true);
    });
    replay.view.unmount();

    server.use(http.post('/api/imports/bank-transactions', () => importResult({ importType: 'BankTransaction' })));
    const bank = renderWorkspace();
    seedMatches(bank.epoch);
    const bankInvoices = ['invoices', bank.epoch, 1, 50] as const;
    await user.click(screen.getByRole('radio', { name: 'Bank transactions' }));
    await submitFile(user, sampleFile(), 'Bank');
    await screen.findByText('Import complete');
    await waitFor(() => {
      expect(queryClient.getQueryState(['matches', bank.epoch, 'smart'])?.isInvalidated).toBe(true);
    });
    expect(queryClient.getQueryState(bankInvoices)?.isInvalidated).toBe(false);
    bank.view.unmount();

    server.use(
      http.post('/api/imports/bank-transactions', () => importResult({ importType: 'BankTransaction', replayed: true })),
    );
    const bankReplay = renderWorkspace();
    seedMatches(bankReplay.epoch);
    await user.click(screen.getByRole('radio', { name: 'Bank transactions' }));
    await submitFile(user, sampleFile(), 'Bank');
    await screen.findByText(/Nothing was added again/);
    await waitFor(() => {
      expect(queryClient.getQueryState(['matches', bankReplay.epoch, 'exact'])?.isInvalidated).toBe(true);
    });
    bankReplay.view.unmount();

    server.use(http.post('/api/imports/invoices', () => HttpResponse.error()));
    const unknown = renderWorkspace();
    seedMatches(unknown.epoch);
    await submitFile(user, sampleFile());
    await screen.findByText(/We don’t know whether this file was saved/);
    expect(queryClient.getQueryState(['matches', unknown.epoch, 'exact'])?.isInvalidated).toBe(false);
    expect(queryClient.getQueryState(['matches', unknown.epoch, 'smart'])?.isInvalidated).toBe(false);
  });

  it('shows an exact size for a small file and a binary size for a larger file', async () => {
    const user = userEvent.setup();
    renderWorkspace();

    await user.upload(
      screen.getByLabelText('CSV file'),
      new File([new Uint8Array(12)], 'small.csv', { type: 'text/csv' }),
    );
    expect(screen.getByText('small.csv · 12 bytes')).toBeInTheDocument();

    await user.upload(
      screen.getByLabelText('CSV file'),
      new File([new Uint8Array(1536)], 'medium.csv', { type: 'text/csv' }),
    );
    expect(screen.getByText('medium.csv · 1.5 KiB')).toBeInTheDocument();
  });

  it('links to invoices when matched rows already existed', async () => {
    const user = userEvent.setup();
    server.use(
      http.post('/api/imports/invoices', () =>
        importResult({
          importedRows: 0,
          alreadyExistsRows: 2,
          rejectedRows: 0,
          totalRows: 2,
        }),
      ),
    );
    renderWorkspace();
    await submitFile(user, sampleFile());

    expect(await screen.findByRole('link', { name: 'Review invoices' })).toHaveAttribute('href', '/invoices');
    expect(counterValue('Added')).toBe('0');
    expect(counterValue('Already in workspace')).toBe('2');
    expect(screen.getByText(/Rows already in your workspace were left unchanged/)).toBeInTheDocument();
    expect(
      screen.getByText('All rows have been checked. Review the totals below.'),
    ).toBeInTheDocument();
  });

  it('does not submit the form when technical details are expanded from the keyboard', async () => {
    const user = userEvent.setup();
    let calls = 0;
    server.use(
      http.post('/api/imports/invoices', () => {
        calls += 1;
        return importResult();
      }),
    );
    renderWorkspace();
    await submitFile(user, sampleFile());

    const details = await screen.findByRole('button', { name: 'Technical details' });
    expect(details).toHaveAttribute('type', 'button');
    expect(details).toHaveAttribute('aria-expanded', 'false');
    expect(calls).toBe(1);

    details.focus();
    await user.keyboard('{Enter}');

    expect(details).toHaveAttribute('aria-expanded', 'true');
    expect(screen.getByRole('region', { name: 'Technical details' })).toBeVisible();
    expect(screen.getByText(`Batch ID: ${batchId}`)).toBeVisible();
    expect(screen.getByText(`SHA-256: ${fileHash}`)).toBeVisible();
    expect(screen.getByText('Created: 2026-10-29T12:00:00+00:00')).toBeVisible();
    expect(screen.getByText('Completed: 2026-10-29T12:00:01+00:00')).toBeVisible();
    expect(screen.getByText('Import type: Invoice')).toBeVisible();
    expect(screen.getByText('Status: Completed')).toBeVisible();
    expect(calls).toBe(1);

    await user.keyboard('{Enter}');
    expect(details).toHaveAttribute('aria-expanded', 'false');
    expect(calls).toBe(1);
  });

  it('keeps the result context separate from subsequent form edits', async () => {
    const user = userEvent.setup();
    server.use(http.post('/api/imports/invoices', () => importResult()));
    renderWorkspace();
    await submitFile(user, sampleFile());

    expect(await screen.findByText('notes.txt · Invoices · Source: DEMO')).toBeVisible();
    await user.click(screen.getByRole('radio', { name: 'Bank transactions' }));
    await user.clear(screen.getByLabelText('File source'));
    await user.type(screen.getByLabelText('File source'), 'OTHER');

    expect(screen.getByText('notes.txt · Invoices · Source: DEMO')).toBeVisible();
    expect(screen.getByRole('link', { name: 'Review invoices' })).toHaveAttribute('href', '/invoices');
  });

  it('shows an unfamiliar row message and code without invented guidance', async () => {
    const user = userEvent.setup();
    server.use(http.post('/api/imports/invoices', () => importResult({
      status: 'Rejected', importedRows: 0, rejectedRows: 1,
      rowErrors: [{ ...rowError(1, 2), errorCode: 'reconciliation.import.new_error', message: 'Diagnostic message.' }],
    })));
    renderWorkspace();
    await submitFile(user, sampleFile());

    expect(await screen.findByText('Diagnostic message.')).toBeVisible();
    expect(screen.getByText('reconciliation.import.new_error')).toBeVisible();
    expect(screen.queryByText(/Check this row in your file/)).not.toBeInTheDocument();
    expect(screen.queryByText(/More information is available in Technical details/)).not.toBeInTheDocument();
  });

  it('does not present server diagnostics as the main error message', async () => {
    const user = userEvent.setup();
    server.use(http.post('/api/imports/invoices', () => HttpResponse.json({
      title: 'Internal processing failure', detail: 'Diagnostic-only text.',
    }, { status: 500 })));
    renderWorkspace();
    await submitFile(user, sampleFile());

    const failure = await screen.findByRole('alert');
    expect(failure).toHaveTextContent('We couldn’t confirm the import. Try the same file again later.');
    expect(failure).toHaveTextContent('Internal processing failure');
    expect(failure).toHaveTextContent('Diagnostic-only text.');
    expect(screen.queryByRole('heading', { name: 'Import result' })).not.toBeInTheDocument();
  });
});

function seedMatches(epoch: number) {
  queryClient.setQueryData(['matches', epoch, 'exact'], { algorithmVersion: 'exact-1' });
  queryClient.setQueryData(['matches', epoch, 'smart'], { algorithmVersion: 'smart-1' });
}

function renderWorkspace() {
  sessionStore.rotate('memory-token', Math.floor(Date.now() / 1000) + 3600, null);
  const epoch = sessionStore.getSnapshot().epoch;
  const key = ['invoices', epoch, 1, 50] as const;
  queryClient.setQueryData(key, { totalCount: 0 });
  const view = render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>
        <ImportWorkspacePage />
      </MemoryRouter>
    </QueryClientProvider>,
  );
  return { epoch, view };
}

async function openTechnicalDetails(user: ReturnType<typeof userEvent.setup>) {
  const details = await screen.findByRole('button', { name: 'Technical details' });
  if (details.getAttribute('aria-expanded') !== 'true') {
    await user.click(details);
  }
}

function counterValue(label: string): string {
  const value = screen.getByText(label).parentElement?.querySelector('dd');
  return value?.textContent ?? '';
}

async function submitFile(user: ReturnType<typeof userEvent.setup>, file: File, sourceSystem = 'Demo') {
  const source = screen.getByLabelText('File source');
  await user.clear(source);
  await user.click(source);
  await user.paste(sourceSystem);
  await user.upload(screen.getByLabelText('CSV file'), file);
  await user.click(screen.getByRole('button', { name: 'Import file' }));
}

function sampleFile(): File {
  return new File(['ExternalId,InvoiceNumber,Amount,Currency\r\n'], 'notes.txt', { type: 'text/csv' });
}

function importResult(overrides: Record<string, unknown> = {}) {
  const body = importBody(overrides);
  return HttpResponse.json(body, { status: body.replayed === true ? 200 : 201 });
}

function importBody(overrides: Record<string, unknown> = {}) {
  return {
    importBatchId: batchId,
    importType: 'Invoice',
    sourceSystem: 'DEMO',
    fileHash,
    fileName: 'notes.txt',
    status: 'Completed',
    totalRows: 1,
    importedRows: 1,
    alreadyExistsRows: 0,
    rejectedRows: 0,
    createdAt: '2026-10-29T12:00:00+00:00',
    completedAt: '2026-10-29T12:00:01+00:00',
    replayed: false,
    rowErrors: [],
    ...overrides,
  };
}

function rowError(dataRowIndex: number, sourceLineNumber: number) {
  return {
    dataRowIndex,
    sourceLineNumber,
    errorCode: 'reconciliation.import.invalid_amount',
    message: 'Amount must be a positive decimal(19,4) value without rounding.',
    columnName: 'Amount',
  };
}
