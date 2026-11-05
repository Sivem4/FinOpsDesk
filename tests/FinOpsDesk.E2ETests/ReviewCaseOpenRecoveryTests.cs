using System.Text.Json;
using System.Text.RegularExpressions;

using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace FinOpsDesk.E2ETests;

[Collection(E2ECollection.Name)]
public sealed class ReviewCaseOpenRecoveryTests
{
    private const string AttemptKey = "finopsdesk.review-case-open-attempt";

    private static readonly Regex OpenUrl = new(@"/api/review-cases$", RegexOptions.None);

    [Fact]
    public async Task Committed_review_case_open_survives_a_lost_response_and_replays()
    {
        await E2ETestRun.RunAsync(TimeSpan.FromSeconds(240), async host =>
        {
            (Guid invoiceId, Guid bankTransactionId) = await SeedDocumentsAsync(host.ConnectionString, host.OrganizationId);
            await host.SignInAsync();
            await host.Page.GetByRole(AriaRole.Link, new() { Name = "Review cases", Exact = true }).ClickAsync();
            await host.Page.GetByLabel("Reason").ClickAsync();
            await host.Page.GetByRole(AriaRole.Option, new() { Name = "CompetingMatch", Exact = true }).ClickAsync();
            await host.Page.GetByLabel("Opening invoice id").FillAsync(invoiceId.ToString("D"));
            await host.Page.GetByLabel("Opening bank transaction id").FillAsync(bankTransactionId.ToString("D"));
            await host.Page.GetByLabel("Opening note").FillAsync("desk open");

            E2ECapturedRequest captured;
            await using (E2ELossSession loss = await E2ELossSession.ArmAsync(
                host.Page,
                OpenUrl,
                () => HasSingleOpenAsync(host.ConnectionString, host.OrganizationId),
                (request, response) => ProveOpenCommitAsync(host, request, response, invoiceId, bankTransactionId)))
            {
                await host.Page.GetByRole(AriaRole.Button, new() { Name = "Open review case", Exact = true }).ClickAsync();
                captured = await loss.WaitAsync();
                Assert.Equal(1, loss.PostCount);
                Assert.Equal(0, loss.UnexpectedPosts);
                await Assertions.Expect(host.Page.GetByText(
                    "This opening has no verified result. Sending it again uses the same request.")).ToBeVisibleAsync();
                await Assertions.Expect(host.Page.GetByText("Review case opened.")).ToHaveCountAsync(0);
                await E2EBrowser.RequireStoredRequestAsync(host.Page, AttemptKey, captured);
            }

            await using (E2EPostWatch watch = await E2ERecovery.WatchReloadAndSignInAsync(host, OpenUrl))
            {
                await Assertions.Expect(host.Page.GetByRole(AriaRole.Button, new() { Name = "Send the same opening again" })).ToBeVisibleAsync();
                await E2EBrowser.RequireStoredRequestAsync(host.Page, AttemptKey, captured);
                Assert.Equal(0, watch.Count);
            }

            await E2ELostResponse.ReplayAsync(
                host.Page,
                OpenUrl,
                captured,
                () => host.Page.GetByRole(AriaRole.Button, new() { Name = "Send the same opening again" }).ClickAsync(),
                response => RequireOpenReplayAsync(response));
            await Assertions.Expect(host.Page.GetByText(
                "This opening already names an existing review case.")).ToBeVisibleAsync();
            await RequireSingleOpenAsync(host.ConnectionString, host.OrganizationId, captured.IdempotencyKey, "desk open");
            Assert.Null(await E2EBrowser.ReadSessionAsync(host.Page, AttemptKey));
            host.AssertBrowserIsQuiet(allowAbortedRequests: true);
        });
    }

    private static async Task ProveOpenCommitAsync(
        E2EHost host,
        E2ECapturedRequest request,
        IAPIResponse response,
        Guid invoiceId,
        Guid bankTransactionId)
    {
        E2EBrowser.RequireNoOrganizationId(request, host.OrganizationId);
        using JsonDocument document = JsonDocument.Parse(await response.TextAsync());
        JsonElement root = document.RootElement;
        string? reviewCaseId = root.GetProperty("reviewCaseId").GetString();
        string? status = root.GetProperty("status").GetString();
        string? note = root.GetProperty("note").GetString();
        bool identity = response.Status == 201
            && Guid.TryParse(reviewCaseId, out Guid parsed)
            && parsed != Guid.Empty
            && string.Equals(status, "Open", StringComparison.Ordinal)
            && string.Equals(note, "desk open", StringComparison.Ordinal)
            && string.Equals(root.GetProperty("invoiceId").GetString(), invoiceId.ToString("D"), StringComparison.OrdinalIgnoreCase)
            && string.Equals(root.GetProperty("bankTransactionId").GetString(), bankTransactionId.ToString("D"), StringComparison.OrdinalIgnoreCase);
        bool committed = await HasSingleOpenAsync(host.ConnectionString, host.OrganizationId);
        if (!identity || !committed)
        {
            throw new E2ECommitUnprovenException(
                "The review case open was not a committed HTTP 201 before the response was dropped.");
        }

        await RequireSingleOpenAsync(host.ConnectionString, host.OrganizationId, request.IdempotencyKey, "desk open");
    }

    private static async Task RequireOpenReplayAsync(IAPIResponse response)
    {
        using JsonDocument document = JsonDocument.Parse(await response.TextAsync());
        JsonElement root = document.RootElement;
        if (response.Status != 200
            || !string.Equals(root.GetProperty("status").GetString(), "Open", StringComparison.Ordinal)
            || !Guid.TryParse(root.GetProperty("reviewCaseId").GetString(), out Guid reviewCaseId)
            || reviewCaseId == Guid.Empty)
        {
            throw new E2ECommitUnprovenException("The review case replay was not HTTP 200 for the original opening.");
        }
    }

    private static async Task<bool> HasSingleOpenAsync(string connectionString, Guid organizationId)
    {
        (int cases, int opened, int attempts) = await CountAsync(connectionString, organizationId);
        return cases == 1 && opened == 1 && attempts == 1;
    }

    private static async Task RequireSingleOpenAsync(
        string connectionString,
        Guid organizationId,
        string idempotencyKey,
        string note)
    {
        OrganizationId tenant = new(organizationId);
        await using ReconciliationDbContext context = E2EDatabase.CreateContext(connectionString);
        int cases = await context.ReviewCases.CountAsync(item => item.OrganizationId == tenant);
        int opened = await context.ReviewCaseAuditEntries.CountAsync(item =>
            item.OrganizationId == tenant && item.Action == ReviewCaseAuditAction.Opened);
        ReviewCaseOpenAttempt attempt = await context.ReviewCaseOpenAttempts.SingleAsync(item =>
            item.OrganizationId == tenant);
        Assert.Equal(1, cases);
        Assert.Equal(1, opened);
        Assert.Equal(Guid.Parse(idempotencyKey), attempt.IdempotencyKey);
        Assert.Equal(note, attempt.Note);
        Assert.Equal(ReviewCaseReason.CompetingMatch, attempt.Reason);
    }

    private static async Task<(int Cases, int Opened, int Attempts)> CountAsync(string connectionString, Guid organizationId)
    {
        OrganizationId tenant = new(organizationId);
        await using ReconciliationDbContext context = E2EDatabase.CreateContext(connectionString);
        int cases = await context.ReviewCases.CountAsync(item => item.OrganizationId == tenant);
        int opened = await context.ReviewCaseAuditEntries.CountAsync(item =>
            item.OrganizationId == tenant && item.Action == ReviewCaseAuditAction.Opened);
        int attempts = await context.ReviewCaseOpenAttempts.CountAsync(item => item.OrganizationId == tenant);
        return (cases, opened, attempts);
    }

    private static async Task<(Guid InvoiceId, Guid BankTransactionId)> SeedDocumentsAsync(
        string connectionString,
        Guid organizationId)
    {
        OrganizationId tenant = new(organizationId);
        CurrencyCode pln = new("PLN");
        Invoice invoice = new(InvoiceId.New(), tenant, "E2E-REVIEW-OPEN", new Money(10m, pln));
        BankTransaction bank = new(
            BankTransactionId.New(),
            tenant,
            new Money(10m, pln),
            new DateOnly(2026, 11, 5),
            "E2E-REVIEW-OPEN");
        await using ReconciliationDbContext context = E2EDatabase.CreateContext(connectionString);
        context.Invoices.Add(invoice);
        context.BankTransactions.Add(bank);
        await context.SaveChangesAsync();
        return (invoice.Id.Value, bank.Id.Value);
    }
}