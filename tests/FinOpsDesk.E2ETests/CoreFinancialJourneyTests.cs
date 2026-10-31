using System.Text;
using System.Text.RegularExpressions;

using FinOpsDesk.E2ETests;
using FinOpsDesk.Modules.Reconciliation.Application.Imports;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.FinancialAudit;
using FinOpsDesk.Modules.Reconciliation.Domain.Imports;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace FinOpsDesk.E2ETests;

public sealed class CoreFinancialJourneyTests : IAsyncLifetime
{
    private const decimal Amount = 40.2500m;

    private E2EHost? _host;

    public async Task InitializeAsync()
    {
        _host = await E2EHost.StartAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }
    }

    [Fact]
    public async Task Import_replay_and_exact_match_persist_one_allocation()
    {
        E2EHost host = _host ?? throw new InvalidOperationException("E2E host was not started.");
        JourneyFixture fixture = JourneyFixture.Create(host.FixtureDirectory);
        Exception? journeyFailure = null;
        try
        {
            await host.RunWithinLimitAsync(async () =>
            {
                await host.SignInAsync();
                await RunJourneyAsync(host, fixture);
                host.AssertBrowserIsQuiet();
            });
        }
        catch (Exception exception)
        {
            journeyFailure = exception;
        }

        Exception? cleanupFailure = null;
        try
        {
            await host.DisposeAsync();
        }
        catch (Exception exception)
        {
            cleanupFailure = exception;
        }

        if (journeyFailure is not null || cleanupFailure is not null)
        {
            throw new Xunit.Sdk.XunitException(host.Describe(string.Join(
                Environment.NewLine,
                journeyFailure?.ToString() ?? string.Empty,
                cleanupFailure?.ToString() ?? string.Empty)));
        }

        Assert.Contains("database dropped " + host.DatabaseName, host.CleanupReport, StringComparison.Ordinal);
        Assert.Contains("api stopped", host.CleanupReport, StringComparison.Ordinal);
        Assert.Contains("vite stopped", host.CleanupReport, StringComparison.Ordinal);
        Assert.False(ProcessTermination.IsAlive(host.ApiProcessId));
        Assert.False(ProcessTermination.IsAlive(host.ViteProcessId));
    }

    private static async Task RunJourneyAsync(E2EHost host, JourneyFixture fixture)
    {
        string batchId = await ImportInvoicesAsync(host.Page, fixture);
        await ExpectInvoiceInRegisterAsync(host.Page, fixture);
        await ImportBankTransactionAsync(host.Page, fixture);
        await ExpectBankTransactionInRegisterAsync(host.Page, fixture);
        await ReplayInvoiceImportAsync(host.Page, fixture, batchId);
        (Guid invoiceId, Guid bankTransactionId) = await ConfirmExactMatchAsync(host.Page, fixture);
        await ExpectHistoryAndAuditAsync(host.Page, fixture);
        await AssertPersistedStateAsync(host, fixture, invoiceId, bankTransactionId);
    }

    private static async Task<string> ImportInvoicesAsync(IPage page, JourneyFixture fixture)
    {
        await page.GetByRole(AriaRole.Link, new() { Name = "Imports" }).ClickAsync();
        await page.GetByRole(AriaRole.Radio, new() { Name = "Invoices" }).CheckAsync();
        await page.GetByRole(AriaRole.Textbox, new() { Name = "File source" }).FillAsync(fixture.SourceSystem);
        await page.Locator("#import-file").SetInputFilesAsync(fixture.InvoiceFile);
        await page.GetByRole(AriaRole.Button, new() { Name = "Import file" }).ClickAsync();
        await Assertions.Expect(page.GetByText("Some rows need attention")).ToBeVisibleAsync();
        await Assertions.Expect(Count(page, "Added")).ToHaveTextAsync("1");
        await Assertions.Expect(Count(page, "Already in workspace")).ToHaveTextAsync("0");
        await Assertions.Expect(Count(page, "Needs attention")).ToHaveTextAsync("1");
        await Assertions.Expect(Count(page, "Rows in file")).ToHaveTextAsync("2");
        await Assertions.Expect(page.GetByText("reconciliation.import.invalid_amount")).ToBeVisibleAsync();
        await ExpandTechnicalDetailsAsync(page);
        await Assertions.Expect(page.GetByText("Status: CompletedWithErrors", new() { Exact = true })).ToBeVisibleAsync();
        return await ReadBatchIdAsync(page);
    }

    private static async Task ExpectInvoiceInRegisterAsync(IPage page, JourneyFixture fixture)
    {
        await page.GetByRole(AriaRole.Link, new() { Name = "Review invoices" }).ClickAsync();
        ILocator table = page.GetByRole(AriaRole.Table, new() { Name = "Invoices" });
        await Assertions.Expect(table.GetByText(fixture.InvoiceNumber, new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByText(fixture.InvalidInvoiceNumber, new() { Exact = true })).ToHaveCountAsync(0);
    }

    private static async Task ImportBankTransactionAsync(IPage page, JourneyFixture fixture)
    {
        await page.GetByRole(AriaRole.Link, new() { Name = "Imports" }).ClickAsync();
        await page.GetByRole(AriaRole.Radio, new() { Name = "Bank transactions" }).CheckAsync();
        await page.GetByRole(AriaRole.Textbox, new() { Name = "File source" }).FillAsync(fixture.SourceSystem);
        await page.Locator("#import-file").SetInputFilesAsync(fixture.BankFile);
        await page.GetByRole(AriaRole.Button, new() { Name = "Import file" }).ClickAsync();
        await Assertions.Expect(page.GetByText("Import complete")).ToBeVisibleAsync();
        await Assertions.Expect(Count(page, "Added")).ToHaveTextAsync("1");
        await Assertions.Expect(Count(page, "Needs attention")).ToHaveTextAsync("0");
        await Assertions.Expect(Count(page, "Rows in file")).ToHaveTextAsync("1");
        await ExpandTechnicalDetailsAsync(page);
        await Assertions.Expect(page.GetByText("Status: Completed", new() { Exact = true })).ToBeVisibleAsync();
    }

    private static async Task ExpectBankTransactionInRegisterAsync(IPage page, JourneyFixture fixture)
    {
        await page.GetByRole(AriaRole.Link, new() { Name = "Review bank transactions" }).ClickAsync();
        ILocator table = page.GetByRole(AriaRole.Table, new() { Name = "Bank transactions" });
        await Assertions.Expect(table.GetByText(fixture.InvoiceNumber, new() { Exact = true })).ToBeVisibleAsync();
    }

    private static async Task ReplayInvoiceImportAsync(IPage page, JourneyFixture fixture, string batchId)
    {
        await page.GetByRole(AriaRole.Link, new() { Name = "Imports" }).ClickAsync();
        await page.GetByRole(AriaRole.Radio, new() { Name = "Invoices" }).CheckAsync();
        await page.GetByRole(AriaRole.Textbox, new() { Name = "File source" }).FillAsync(fixture.SourceSystem);
        await page.Locator("#import-file").SetInputFilesAsync(fixture.InvoiceFile);
        await page.GetByRole(AriaRole.Button, new() { Name = "Import file" }).ClickAsync();
        await Assertions.Expect(page.GetByText("You\u2019ve already imported this file")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByText("Nothing was added again.")).ToBeVisibleAsync();
        await ExpandTechnicalDetailsAsync(page);
        await Assertions.Expect(page.GetByText("Status: CompletedWithErrors", new() { Exact = true })).ToBeVisibleAsync();
        Assert.Equal(batchId, await ReadBatchIdAsync(page));
    }

    private static async Task<(Guid InvoiceId, Guid BankTransactionId)> ConfirmExactMatchAsync(
        IPage page,
        JourneyFixture fixture)
    {
        await page.GetByRole(AriaRole.Link, new() { Name = "Reconciliation" }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Exact matches (1)" })).ToBeVisibleAsync();
        ILocator region = page.GetByRole(AriaRole.Region, new() { Name = "Exact matches" });
        await Assertions.Expect(region.GetByText(fixture.InvoiceNumber, new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(region.GetByText(new Regex(@"40\.25(00)? PLN"))).ToBeVisibleAsync();
        ILocator confirm = region.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^Confirm exact match for invoice ") });
        await Assertions.Expect(confirm).ToHaveCountAsync(1);
        string label = await confirm.GetAttributeAsync("aria-label")
            ?? throw new InvalidOperationException("Exact-1 confirm button has no accessible name.");
        Match match = Regex.Match(
            label,
            "^Confirm exact match for invoice (?<invoice>[0-9a-fA-F-]{36}) and bank transaction (?<bank>[0-9a-fA-F-]{36})$");
        Assert.True(match.Success, "Exact-1 confirm button did not name one invoice and one bank transaction.");
        Guid invoiceId = Guid.Parse(match.Groups["invoice"].Value);
        Guid bankTransactionId = Guid.Parse(match.Groups["bank"].Value);

        await confirm.ClickAsync();
        ILocator dialog = page.GetByRole(AriaRole.Dialog);
        await Assertions.Expect(dialog.GetByText("Reference " + fixture.InvoiceNumber)).ToBeVisibleAsync();
        await Assertions.Expect(dialog.GetByText("Invoice " + invoiceId.ToString("D"))).ToBeVisibleAsync();
        await Assertions.Expect(dialog.GetByText("Bank transaction " + bankTransactionId.ToString("D"))).ToBeVisibleAsync();
        await Assertions.Expect(dialog.GetByText(new Regex(@"40\.25(00)? PLN"))).ToBeVisibleAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Confirm allocation" }).ClickAsync();
        await Assertions.Expect(dialog.GetByText("Allocation recorded.")).ToBeVisibleAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Close" }).ClickAsync();
        await Assertions.Expect(dialog).ToBeHiddenAsync();
        return (invoiceId, bankTransactionId);
    }

    private static async Task ExpectHistoryAndAuditAsync(IPage page, JourneyFixture fixture)
    {
        await page.GetByRole(AriaRole.Link, new() { Name = "Allocation history" }).ClickAsync();
        ILocator table = page.GetByRole(AriaRole.Table, new() { Name = "Allocation history" });
        await Assertions.Expect(table.GetByText(fixture.InvoiceNumber, new() { Exact = true })).ToHaveCountAsync(2);
        await Assertions.Expect(table.GetByRole(AriaRole.Cell, new() { Name = "Confirmed", Exact = true })).ToHaveCountAsync(1);
        await Assertions.Expect(table.GetByText(new Regex(@"40\.25(00)? PLN"))).ToBeVisibleAsync();
        await table.GetByRole(AriaRole.Button, new() { Name = "View audit" }).ClickAsync();
        ILocator audit = page.GetByRole(AriaRole.Table, new() { NameRegex = new Regex("^Audit entries for allocation ") });
        await Assertions.Expect(audit.GetByRole(AriaRole.Cell, new() { Name = "Confirmation", Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(audit.GetByRole(AriaRole.Cell, new() { Name = "Exact match", Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(audit.GetByRole(AriaRole.Cell, new() { Name = E2EAccessToken.ActorId, Exact = true })).ToBeVisibleAsync();
    }

    private static async Task AssertPersistedStateAsync(
        E2EHost host,
        JourneyFixture fixture,
        Guid invoiceId,
        Guid bankTransactionId)
    {
        OrganizationId organizationId = new(host.OrganizationId);
        await using ReconciliationDbContext context = E2EDatabase.CreateContext(host.ConnectionString);
        List<Invoice> invoices = await context.Invoices.AsNoTracking()
            .Where(invoice => invoice.OrganizationId == organizationId)
            .ToListAsync();
        List<BankTransaction> bankTransactions = await context.BankTransactions.AsNoTracking()
            .Where(transaction => transaction.OrganizationId == organizationId)
            .ToListAsync();
        List<Allocation> allocations = await context.Allocations.AsNoTracking()
            .Where(allocation => allocation.OrganizationId == organizationId)
            .ToListAsync();
        List<FinancialAuditEntry> audit = await context.FinancialAuditEntries.AsNoTracking()
            .Where(entry => entry.OrganizationId == organizationId)
            .ToListAsync();
        List<ImportBatch> batches = await context.ImportBatches.AsNoTracking()
            .Include(batch => batch.RowErrors)
            .Where(batch => batch.OrganizationId == organizationId)
            .ToListAsync();

        Invoice invoice = Assert.Single(invoices);
        Assert.Equal(invoiceId, invoice.Id.Value);
        Assert.Equal(fixture.InvoiceNumber, invoice.InvoiceNumber);
        Assert.Equal(fixture.ValidExternalId, invoice.SourceIdentity?.ExternalId.Value);
        Assert.Equal("E2E", invoice.SourceIdentity?.SourceSystem.Value);
        Assert.Equal(Amount, invoice.TotalAmount.Amount);
        Assert.Equal("PLN", invoice.TotalAmount.Currency.Value);
        Assert.Equal(Amount, invoice.AllocatedAmount.Amount);
        Assert.Equal(0m, invoice.OutstandingAmount.Amount);
        Assert.DoesNotContain(invoices, stored => stored.InvoiceNumber == fixture.InvalidInvoiceNumber);

        BankTransaction bankTransaction = Assert.Single(bankTransactions);
        Assert.Equal(bankTransactionId, bankTransaction.Id.Value);
        Assert.Equal(fixture.InvoiceNumber, bankTransaction.Reference);
        Assert.Equal(fixture.BankExternalId, bankTransaction.SourceIdentity?.ExternalId.Value);
        Assert.Equal(Amount, bankTransaction.Amount.Amount);
        Assert.Equal("PLN", bankTransaction.Amount.Currency.Value);
        Assert.Equal(Amount, bankTransaction.AllocatedAmount.Amount);
        Assert.Equal(0m, bankTransaction.UnappliedAmount.Amount);

        Allocation allocation = Assert.Single(allocations);
        Assert.Equal(invoice.Id, allocation.InvoiceId);
        Assert.Equal(bankTransaction.Id, allocation.BankTransactionId);
        Assert.Equal(AllocationStatus.Confirmed, allocation.Status);
        Assert.Equal(Amount, allocation.Amount.Amount);
        Assert.Equal("PLN", allocation.Amount.Currency.Value);
        Assert.Equal(AllocationIdempotencyOperation.ExactMatch, allocation.IdempotencyOperation);

        FinancialAuditEntry entry = Assert.Single(audit);
        Assert.Equal(allocation.Id, entry.AllocationId);
        Assert.Equal(FinancialAuditAction.Confirmation, entry.Action);
        Assert.Equal(AllocationIdempotencyOperation.ExactMatch, entry.IdempotencyOperation);
        Assert.Equal(E2EAccessToken.ActorId, entry.ActorId);
        Assert.Equal(Amount, entry.Amount.Amount);
        Assert.Equal("PLN", entry.Amount.Currency.Value);
        Assert.Null(entry.Reason);
        Assert.Null(entry.BatchExecutionId);

        Assert.Equal(2, batches.Count);
        ImportBatch invoiceBatch = Assert.Single(batches, batch => batch.ImportType == ImportType.Invoice);
        Assert.Equal(ImportBatchStatus.CompletedWithErrors, invoiceBatch.Status);
        Assert.Equal(1, invoiceBatch.ImportedRows);
        Assert.Equal(1, invoiceBatch.RejectedRows);
        ImportRowError rowError = Assert.Single(invoiceBatch.RowErrors);
        Assert.Equal("Amount", rowError.ColumnName);
        Assert.Equal(ImportRowErrors.InvalidAmount.Code, rowError.ErrorCode);
        ImportBatch bankBatch = Assert.Single(batches, batch => batch.ImportType == ImportType.BankTransaction);
        Assert.Equal(ImportBatchStatus.Completed, bankBatch.Status);
        Assert.Equal(1, bankBatch.ImportedRows);
        Assert.Equal(0, bankBatch.RejectedRows);
        Assert.Empty(bankBatch.RowErrors);
    }

    private static ILocator Count(IPage page, string term)
    {
        return page.Locator("dt").Filter(new() { HasText = term }).Locator("xpath=following-sibling::dd[1]");
    }

    private static async Task ExpandTechnicalDetailsAsync(IPage page)
    {
        ILocator summary = page.GetByRole(AriaRole.Button, new() { Name = "Technical details" });
        if (await summary.GetAttributeAsync("aria-expanded") != "true")
        {
            await summary.ClickAsync();
        }
    }

    private static async Task<string> ReadBatchIdAsync(IPage page)
    {
        await ExpandTechnicalDetailsAsync(page);
        ILocator batch = page.GetByText(new Regex(@"^Batch ID: [0-9a-fA-F-]{36}$"));
        await Assertions.Expect(batch).ToBeVisibleAsync();
        string text = (await batch.InnerTextAsync()).Trim();
        const string marker = "Batch ID: ";
        Assert.StartsWith(marker, text, StringComparison.Ordinal);
        return text[marker.Length..];
    }

    private sealed record JourneyFixture(
        string SourceSystem,
        string InvoiceNumber,
        string InvalidInvoiceNumber,
        string ValidExternalId,
        string InvalidExternalId,
        string BankExternalId,
        string InvoiceFile,
        string BankFile)
    {
        public static JourneyFixture Create(string directory)
        {
            string token = Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
            string invoiceNumber = "E2E" + token;
            string validExternalId = "inv-" + token;
            string invalidExternalId = "bad-" + token;
            string bankExternalId = "txn-" + token;
            string invoiceFile = Path.Combine(directory, "invoices.csv");
            string bankFile = Path.Combine(directory, "bank.csv");
            File.WriteAllText(
                invoiceFile,
                "ExternalId,InvoiceNumber,Amount,Currency\n"
                + $"{validExternalId},{invoiceNumber},40.2500,PLN\n"
                + $"{invalidExternalId},{invoiceNumber}-X,0,PLN\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.WriteAllText(
                bankFile,
                "ExternalId,BookingDate,Amount,Currency,Reference\n"
                + $"{bankExternalId},2026-10-31,40.2500,PLN,{invoiceNumber}\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return new JourneyFixture(
                "E2E",
                invoiceNumber,
                invoiceNumber + "-X",
                validExternalId,
                invalidExternalId,
                bankExternalId,
                invoiceFile,
                bankFile);
        }
    }
}