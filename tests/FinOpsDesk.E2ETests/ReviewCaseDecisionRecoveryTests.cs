using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using FinOpsDesk.Modules.Reconciliation.Application.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.FinancialAudit;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;

namespace FinOpsDesk.E2ETests;

[Collection(E2ECollection.Name)]
public sealed class ReviewCaseDecisionRecoveryTests
{
    private const string AttemptKey = "finopsdesk.review-case-decision-attempt";
    private const string OpenAttemptKey = "finopsdesk.review-case-open-attempt";
    private const decimal Principal = 40m;
    private const decimal Allocated = 15m;
    private const string ReversalReason = "e2e decision reversal";

    private static readonly Regex ResolveUrl = new(
        @"/api/review-cases/[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}/resolve$",
        RegexOptions.None);

    [Fact]
    public async Task Committed_allocation_recorded_survives_reversal_and_replays_the_original_body()
    {
        await E2ETestRun.RunAsync(TimeSpan.FromSeconds(240), async host =>
        {
            SeededDecision seeded = await SeedAsync(host.ConnectionString, host.OrganizationId);
            Books seedBooks = await ReadBooksAsync(host.ConnectionString, host.OrganizationId);
            AssertBooks(seedBooks, outstanding: 25m, unapplied: 25m, confirmations: 1, reversals: 0, AllocationStatus.Confirmed);

            await host.SignInAsync();
            await host.Page.GetByRole(AriaRole.Link, new() { Name = "Review cases", Exact = true }).ClickAsync();
            await host.Page.GetByText("decision seed").WaitForAsync();
            await host.Page.GetByRole(AriaRole.Button, new() { Name = "Show review case", Exact = false }).ClickAsync();
            await host.Page.GetByRole(AriaRole.Button, new() { Name = "Resolve", Exact = true }).ClickAsync();
            await host.Page.GetByLabel("Outcome").ClickAsync();
            await host.Page.GetByRole(AriaRole.Option, new() { Name = "AllocationRecorded", Exact = true }).ClickAsync();
            await host.Page.GetByLabel("Decision allocation id").FillAsync(seeded.AllocationId.ToString("D"));

            CaseSnapshot committed;
            E2ECapturedRequest captured;
            await using (E2ELossSession loss = await E2ELossSession.ArmAsync(
                host.Page,
                ResolveUrl,
                () => IsResolvedAsync(host.ConnectionString, host.OrganizationId, seeded),
                (_, response) => ProveDecisionCommitAsync(host, response, seeded),
                requireIdempotencyKey: false))
            {
                await host.Page.GetByRole(AriaRole.Button, new() { Name = "Resolve review case", Exact = true }).ClickAsync();
                captured = await loss.WaitAsync();
                Assert.Equal(1, loss.PostCount);
                Assert.Equal(0, loss.UnexpectedPosts);
                Assert.Equal(string.Empty, captured.IdempotencyKey);
                RequireDecisionBody(captured, seeded);
                await Assertions.Expect(host.Page.GetByText(
                    "This decision has no verified result. Sending it again uses the same request.")).ToBeVisibleAsync();
                await Assertions.Expect(host.Page.GetByText("The review case matches this decision.")).ToHaveCountAsync(0);
                await RequireStoredDecisionAsync(host.Page, captured, seeded, host.OrganizationId);
                committed = await ReadCaseAsync(host.ConnectionString, host.OrganizationId, seeded.ReviewCaseId);
            }

            Assert.NotEqual(seeded.RequestToken, committed.Token);
            Books afterDecision = await ReadBooksAsync(host.ConnectionString, host.OrganizationId);
            AssertBooks(afterDecision, outstanding: 25m, unapplied: 25m, confirmations: 1, reversals: 0, AllocationStatus.Confirmed);

            await using IAPIResponse reversal = await host.Page.APIRequest.PostAsync(
                new Uri(host.Page.Url).GetLeftPart(UriPartial.Authority)
                    + "/api/allocations/" + seeded.AllocationId.ToString("D") + "/reverse",
                new APIRequestContextOptions
                {
                    Headers = new Dictionary<string, string>
                    {
                        ["Accept"] = "application/json",
                        ["Content-Type"] = "application/json",
                        ["Authorization"] = "Bearer " + host.CreateAccessToken(host.OrganizationId),
                        ["Idempotency-Key"] = Guid.NewGuid().ToString("D"),
                    },
                    Data = "{\"reason\":\"" + ReversalReason + "\"}",
                });
            if (reversal.Status != 200)
            {
                throw new Xunit.Sdk.XunitException(
                    "The allocation reversal returned HTTP " + reversal.Status + ".");
            }

            Books reversed = await ReadBooksAsync(host.ConnectionString, host.OrganizationId);
            AssertBooks(reversed, outstanding: Principal, unapplied: Principal, confirmations: 1, reversals: 1, AllocationStatus.Reversed);
            Assert.Equal(ReversalReason, reversed.ReversalReason);
            Assert.Equal(E2EAccessToken.ActorId, reversed.ReversalActor);
            CaseSnapshot afterReversal = await ReadCaseAsync(host.ConnectionString, host.OrganizationId, seeded.ReviewCaseId);
            AssertSameCase(committed, afterReversal);
            await RequireStoredDecisionAsync(host.Page, captured, seeded, host.OrganizationId);

            await using (E2EPostWatch watch = await E2ERecovery.WatchReloadAndSignInAsync(host, ResolveUrl))
            {
                await Assertions.Expect(host.Page.GetByRole(
                    AriaRole.Button,
                    new() { Name = "Send the same decision again" })).ToBeVisibleAsync();
                await RequireStoredDecisionAsync(host.Page, captured, seeded, host.OrganizationId);
                Assert.Equal(0, watch.Count);
            }

            int detailReadsBeforePost = 0;
            bool posted = false;
            void CountDetailReads(object? _, IRequest request)
            {
                if (string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase)
                    && ResolveUrl.IsMatch(request.Url))
                {
                    posted = true;
                }

                if (!posted
                    && string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase)
                    && request.Url.Contains("/api/review-cases/" + seeded.ReviewCaseId.ToString("D"), StringComparison.OrdinalIgnoreCase)
                    && !request.Url.Contains("/audit", StringComparison.OrdinalIgnoreCase))
                {
                    Interlocked.Increment(ref detailReadsBeforePost);
                }
            }

            host.Page.Request += CountDetailReads;
            try
            {
                await E2ELostResponse.ReplayAsync(
                    host.Page,
                    ResolveUrl,
                    captured,
                    () => host.Page.GetByRole(AriaRole.Button, new() { Name = "Send the same decision again" }).ClickAsync(),
                    response => RequireReplayAsync(response, seeded, committed),
                    requireIdempotencyKey: false);
            }
            finally
            {
                host.Page.Request -= CountDetailReads;
            }

            Assert.Equal(0, detailReadsBeforePost);
            await Assertions.Expect(host.Page.GetByText("The review case matches this decision.")).ToBeVisibleAsync();
            Assert.Null(await E2EBrowser.ReadSessionAsync(host.Page, AttemptKey));
            Assert.Null(await E2EBrowser.ReadSessionAsync(host.Page, OpenAttemptKey));
            CaseSnapshot afterReplay = await ReadCaseAsync(host.ConnectionString, host.OrganizationId, seeded.ReviewCaseId);
            AssertSameCase(committed, afterReplay);
            Books afterReplayBooks = await ReadBooksAsync(host.ConnectionString, host.OrganizationId);
            AssertBooks(afterReplayBooks, outstanding: Principal, unapplied: Principal, confirmations: 1, reversals: 1, AllocationStatus.Reversed);
            Assert.Equal(reversed.ReversalReason, afterReplayBooks.ReversalReason);
            host.AssertBrowserIsQuiet(allowAbortedRequests: true);
        });
    }

    private static void RequireDecisionBody(E2ECapturedRequest request, SeededDecision seeded)
    {
        E2EBrowser.RequireNoOrganizationId(request, seeded.OrganizationId);
        string body = Encoding.UTF8.GetString(request.Body);
        string expected = "{\"outcome\":\"AllocationRecorded\",\"allocationId\":\""
            + seeded.AllocationId.ToString("D")
            + "\",\"concurrencyToken\":\""
            + seeded.RequestToken
            + "\"}";
        Assert.Equal(expected, body);
    }

    private static async Task ProveDecisionCommitAsync(E2EHost host, IAPIResponse response, SeededDecision seeded)
    {
        using JsonDocument document = JsonDocument.Parse(await response.TextAsync());
        JsonElement root = document.RootElement;
        string? responseToken = root.GetProperty("concurrencyToken").GetString();
        bool identity = response.Status == 200
            && string.Equals(root.GetProperty("reviewCaseId").GetString(), seeded.ReviewCaseId.ToString("D"), StringComparison.OrdinalIgnoreCase)
            && string.Equals(root.GetProperty("status").GetString(), "Resolved", StringComparison.Ordinal)
            && string.Equals(root.GetProperty("outcome").GetString(), "AllocationRecorded", StringComparison.Ordinal)
            && string.Equals(root.GetProperty("allocationId").GetString(), seeded.AllocationId.ToString("D"), StringComparison.OrdinalIgnoreCase)
            && root.GetProperty("resolutionNote").ValueKind == JsonValueKind.Null
            && root.TryGetProperty("closedAt", out JsonElement closedAt)
            && closedAt.ValueKind == JsonValueKind.String
            && ReviewCaseConcurrencyToken.TryDecode(responseToken, out _);
        bool committed = await IsResolvedAsync(host.ConnectionString, host.OrganizationId, seeded);
        if (!identity || !committed || string.Equals(responseToken, seeded.RequestToken, StringComparison.Ordinal))
        {
            throw new E2ECommitUnprovenException(
                "The review case decision was not a committed HTTP 200 before the response was dropped.");
        }
    }

    private static async Task RequireReplayAsync(IAPIResponse response, SeededDecision seeded, CaseSnapshot committed)
    {
        using JsonDocument document = JsonDocument.Parse(await response.TextAsync());
        JsonElement root = document.RootElement;
        string? responseToken = root.GetProperty("concurrencyToken").GetString();
        if (response.Status != 200
            || !string.Equals(root.GetProperty("reviewCaseId").GetString(), seeded.ReviewCaseId.ToString("D"), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(root.GetProperty("status").GetString(), "Resolved", StringComparison.Ordinal)
            || !string.Equals(root.GetProperty("outcome").GetString(), "AllocationRecorded", StringComparison.Ordinal)
            || !string.Equals(root.GetProperty("allocationId").GetString(), seeded.AllocationId.ToString("D"), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(responseToken, committed.Token, StringComparison.Ordinal)
            || !ReviewCaseConcurrencyToken.TryDecode(responseToken, out _))
        {
            throw new E2ECommitUnprovenException(
                "The review case replay was not HTTP 200 for the original decision.");
        }
    }

    private static async Task RequireStoredDecisionAsync(
        IPage page,
        E2ECapturedRequest request,
        SeededDecision seeded,
        Guid organizationId)
    {
        string? raw = await E2EBrowser.ReadSessionAsync(page, AttemptKey);
        Assert.False(string.IsNullOrEmpty(raw));
        using JsonDocument document = JsonDocument.Parse(raw!);
        JsonElement root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("ReviewCaseResolve", root.GetProperty("operation").GetString());
        Assert.Equal(organizationId.ToString("D"), root.GetProperty("organizationId").GetString());
        Assert.Equal(seeded.ReviewCaseId.ToString("D"), root.GetProperty("caseId").GetString());
        Assert.Equal("AllocationRecorded", root.GetProperty("outcome").GetString());
        Assert.Equal(seeded.AllocationId.ToString("D"), root.GetProperty("allocationId").GetString());
        Assert.Equal(seeded.RequestToken, root.GetProperty("concurrencyToken").GetString());
        Assert.False(root.TryGetProperty("idempotencyKey", out _));
        string? storedBody = root.GetProperty("requestBody").GetString();
        Assert.NotNull(storedBody);
        Assert.True(request.Body.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(storedBody)));
    }

    private static async Task<bool> IsResolvedAsync(string connectionString, Guid organizationId, SeededDecision seeded)
    {
        CaseSnapshot snapshot = await ReadCaseAsync(connectionString, organizationId, seeded.ReviewCaseId);
        Books books = await ReadBooksAsync(connectionString, organizationId);
        return snapshot.Status == ReviewCaseStatus.Resolved
            && snapshot.Outcome == ReviewCaseOutcome.AllocationRecorded
            && snapshot.AllocationId == seeded.AllocationId
            && snapshot.TerminalEntries == 1
            && snapshot.Token != seeded.RequestToken
            && books.Outstanding == 25m
            && books.Unapplied == 25m
            && books.Confirmations == 1
            && books.Reversals == 0;
    }

    private static async Task<SeededDecision> SeedAsync(string connectionString, Guid organizationId)
    {
        OrganizationId tenant = new(organizationId);
        CurrencyCode pln = new("PLN");
        Invoice invoice = new(InvoiceId.New(), tenant, "E2E-REVIEW-DECISION", new Money(Principal, pln));
        BankTransaction bank = new(
            BankTransactionId.New(),
            tenant,
            new Money(Principal, pln),
            new DateOnly(2026, 11, 5),
            "E2E-REVIEW-DECISION");
        Assert.True(invoice.Allocate(new Money(Allocated, pln)).IsSuccess);
        Assert.True(bank.Allocate(new Money(Allocated, pln)).IsSuccess);
        Allocation allocation = new(
            AllocationId.New(),
            tenant,
            invoice.Id,
            bank.Id,
            new Money(Allocated, pln),
            new DateTimeOffset(2026, 11, 5, 8, 0, 0, TimeSpan.Zero),
            Guid.NewGuid(),
            AllocationIdempotencyOperation.ExplicitAmount);
        FinancialAuditEntry confirmation = FinancialAuditEntry.ForConfirmation(
            FinancialAuditEntryId.New(),
            allocation,
            E2EAccessToken.ActorId,
            batchExecutionId: null);
        ReviewCase reviewCase = ReviewCase.Open(
            ReviewCaseId.New(),
            tenant,
            ReviewCaseReason.CompetingMatch,
            invoice.Id,
            bank.Id,
            sourceLabel: null,
            note: "decision seed",
            openedAt: new DateTimeOffset(2026, 11, 5, 8, 30, 0, TimeSpan.Zero));
        await using ReconciliationDbContext context = E2EDatabase.CreateContext(connectionString);
        context.AddRange(invoice, bank, allocation, confirmation, reviewCase);
        await context.SaveChangesAsync();
        string requestToken = ReviewCaseConcurrencyToken.Encode(
            context.Entry(reviewCase).Property<byte[]>("RowVersion").CurrentValue);
        return new SeededDecision(organizationId, reviewCase.Id.Value, allocation.Id.Value, requestToken);
    }

    private static async Task<CaseSnapshot> ReadCaseAsync(string connectionString, Guid organizationId, Guid reviewCaseId)
    {
        OrganizationId tenant = new(organizationId);
        await using ReconciliationDbContext context = E2EDatabase.CreateContext(connectionString);
        ReviewCase reviewCase = await context.ReviewCases.SingleAsync(item =>
            item.OrganizationId == tenant && item.Id == new ReviewCaseId(reviewCaseId));
        int terminal = await context.ReviewCaseAuditEntries.CountAsync(item =>
            item.OrganizationId == tenant
            && item.ReviewCaseId == reviewCase.Id
            && item.Action == ReviewCaseAuditAction.Resolved);
        int other = await context.ReviewCaseAuditEntries.CountAsync(item =>
            item.OrganizationId == tenant && item.Action != ReviewCaseAuditAction.Resolved);
        byte[] rowVersion = context.Entry(reviewCase).Property<byte[]>("RowVersion").CurrentValue;
        return new CaseSnapshot(
            reviewCase.Status,
            reviewCase.Outcome,
            reviewCase.AllocationId?.Value,
            reviewCase.ClosedAt,
            ReviewCaseConcurrencyToken.Encode(rowVersion),
            terminal,
            other);
    }

    private static async Task<Books> ReadBooksAsync(string connectionString, Guid organizationId)
    {
        OrganizationId tenant = new(organizationId);
        await using ReconciliationDbContext context = E2EDatabase.CreateContext(connectionString);
        Invoice invoice = await context.Invoices.SingleAsync(item => item.OrganizationId == tenant);
        BankTransaction bank = await context.BankTransactions.SingleAsync(item => item.OrganizationId == tenant);
        Allocation allocation = await context.Allocations.SingleAsync(item => item.OrganizationId == tenant);
        List<FinancialAuditEntry> audit = await context.FinancialAuditEntries
            .Where(item => item.OrganizationId == tenant)
            .ToListAsync();
        FinancialAuditEntry? reversal = audit.SingleOrDefault(item => item.Action == FinancialAuditAction.Reversal);
        return new Books(
            invoice.OutstandingAmount.Amount,
            bank.UnappliedAmount.Amount,
            audit.Count(item => item.Action == FinancialAuditAction.Confirmation),
            audit.Count(item => item.Action == FinancialAuditAction.Reversal),
            allocation.Status,
            reversal?.Reason,
            reversal?.ActorId);
    }

    private static void AssertBooks(
        Books books,
        decimal outstanding,
        decimal unapplied,
        int confirmations,
        int reversals,
        AllocationStatus status)
    {
        Assert.Equal(outstanding, books.Outstanding);
        Assert.Equal(unapplied, books.Unapplied);
        Assert.Equal(confirmations, books.Confirmations);
        Assert.Equal(reversals, books.Reversals);
        Assert.Equal(status, books.AllocationStatus);
    }

    private static void AssertSameCase(CaseSnapshot expected, CaseSnapshot actual)
    {
        Assert.Equal(ReviewCaseStatus.Resolved, actual.Status);
        Assert.Equal(expected.Outcome, actual.Outcome);
        Assert.Equal(expected.AllocationId, actual.AllocationId);
        Assert.Equal(expected.ClosedAt, actual.ClosedAt);
        Assert.Equal(expected.Token, actual.Token);
        Assert.Equal(1, actual.TerminalEntries);
        Assert.Equal(0, actual.OtherEntries);
    }

    private sealed record SeededDecision(Guid OrganizationId, Guid ReviewCaseId, Guid AllocationId, string RequestToken);

    private sealed record CaseSnapshot(
        ReviewCaseStatus Status,
        ReviewCaseOutcome? Outcome,
        Guid? AllocationId,
        DateTimeOffset? ClosedAt,
        string Token,
        int TerminalEntries,
        int OtherEntries);

    private sealed record Books(
        decimal Outstanding,
        decimal Unapplied,
        int Confirmations,
        int Reversals,
        AllocationStatus AllocationStatus,
        string? ReversalReason,
        string? ReversalActor);
}