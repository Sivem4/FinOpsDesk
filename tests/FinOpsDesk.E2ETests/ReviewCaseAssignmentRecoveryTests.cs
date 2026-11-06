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
public sealed class ReviewCaseAssignmentRecoveryTests
{
    private const string AttemptKey = "finopsdesk.review-case-assignment-attempt";
    private const string OpenAttemptKey = "finopsdesk.review-case-open-attempt";
    private const string DecisionAttemptKey = "finopsdesk.review-case-decision-attempt";
    private const string CommentAttemptKey = "finopsdesk.review-case-comment-attempt";

    private static readonly Regex AssignmentUrl = new(
        @"/api/review-cases/[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}/assignment$",
        RegexOptions.None);

    [Fact]
    public async Task Committed_review_case_claim_survives_a_lost_response_and_replays()
    {
        await E2ETestRun.RunAsync(TimeSpan.FromSeconds(240), async host =>
        {
            Guid reviewCaseId = await SeedCaseAsync(host.ConnectionString, host.OrganizationId);
            AssignmentProof proof = new();

            await host.SignInAsync();
            await host.Page.GetByRole(AriaRole.Link, new() { Name = "Review cases", Exact = true }).ClickAsync();
            await host.Page.GetByText("assignment seed").WaitForAsync();
            await host.Page.GetByRole(AriaRole.Button, new() { Name = "Show review case", Exact = false }).ClickAsync();

            E2ECapturedRequest captured;
            await using (E2ELossSession loss = await E2ELossSession.ArmAsync(
                host.Page,
                AssignmentUrl,
                () => HasSingleClaimAsync(host.ConnectionString, host.OrganizationId, reviewCaseId),
                (request, response) => ProveClaimCommitAsync(host, request, response, reviewCaseId, proof)))
            {
                await host.Page.GetByRole(AriaRole.Button, new() { Name = "Claim this case", Exact = true }).ClickAsync();
                captured = await loss.WaitAsync();
                Assert.Equal(1, loss.PostCount);
                Assert.Equal(0, loss.UnexpectedPosts);
                await Assertions.Expect(host.Page.GetByText(
                    "This assignment command has no verified result. Sending it again uses the same request.")).ToBeVisibleAsync();
                await Assertions.Expect(host.Page.GetByText("This claim was stored.")).ToHaveCountAsync(0);
                await E2EBrowser.RequireStoredRequestAsync(host.Page, AttemptKey, captured);
            }

            await using (E2EPostWatch watch = await E2ERecovery.WatchReloadAndSignInAsync(host, AssignmentUrl))
            {
                await Assertions.Expect(host.Page.GetByRole(AriaRole.Button, new() { Name = "Send the same claim again" })).ToBeVisibleAsync();
                await E2EBrowser.RequireStoredRequestAsync(host.Page, AttemptKey, captured);
                Assert.Equal(0, watch.Count);
            }

            await E2ELostResponse.ReplayAsync(
                host.Page,
                AssignmentUrl,
                captured,
                () => host.Page.GetByRole(AriaRole.Button, new() { Name = "Send the same claim again" }).ClickAsync(),
                response => RequireClaimReplayAsync(response, reviewCaseId, proof));
            await Assertions.Expect(host.Page.GetByText("This claim was already stored.")).ToBeVisibleAsync();
            await Assertions.Expect(host.Page.GetByText("Historical assignee developmentJwt " + proof.ActorId + " at " + proof.AssignedAt + ".")).ToBeVisibleAsync();
            await Assertions.Expect(host.Page.GetByText("Recorded " + proof.RecordedAt)).ToBeVisibleAsync();
            await RequireSingleClaimAsync(host.ConnectionString, host.OrganizationId, reviewCaseId, captured.IdempotencyKey, proof);
            Assert.Null(await E2EBrowser.ReadSessionAsync(host.Page, AttemptKey));
            Assert.Null(await E2EBrowser.ReadSessionAsync(host.Page, OpenAttemptKey));
            Assert.Null(await E2EBrowser.ReadSessionAsync(host.Page, DecisionAttemptKey));
            Assert.Null(await E2EBrowser.ReadSessionAsync(host.Page, CommentAttemptKey));
            host.AssertBrowserIsQuiet(allowAbortedRequests: true);
        });
    }

    private static async Task ProveClaimCommitAsync(
        E2EHost host,
        E2ECapturedRequest request,
        IAPIResponse response,
        Guid reviewCaseId,
        AssignmentProof proof)
    {
        E2EBrowser.RequireNoOrganizationId(request, host.OrganizationId);
        using JsonDocument document = JsonDocument.Parse(await response.TextAsync());
        JsonElement root = document.RootElement;
        JsonElement assignee = root.GetProperty("assignee");
        string? caseId = root.GetProperty("reviewCaseId").GetString();
        string? operation = root.GetProperty("operation").GetString();
        string? actorSource = assignee.GetProperty("actorSource").GetString();
        string? actorId = assignee.GetProperty("actorId").GetString();
        string? assignedAt = assignee.GetProperty("assignedAt").GetString();
        string? recordedAt = root.GetProperty("recordedAt").GetString();
        bool identity = response.Status == 201
            && Guid.TryParse(caseId, out Guid parsed)
            && parsed == reviewCaseId
            && string.Equals(operation, "claim", StringComparison.Ordinal)
            && string.Equals(actorSource, "developmentJwt", StringComparison.Ordinal)
            && string.Equals(actorId, E2EAccessToken.ActorId, StringComparison.Ordinal)
            && assignedAt is not null
            && recordedAt is not null;
        proof.ActorId = actorId ?? string.Empty;
        proof.AssignedAt = assignedAt ?? string.Empty;
        proof.RecordedAt = recordedAt ?? string.Empty;
        bool committed = await HasSingleClaimAsync(host.ConnectionString, host.OrganizationId, reviewCaseId);
        if (!identity || !committed)
        {
            throw new E2ECommitUnprovenException(
                "The review case claim was not a committed HTTP 201 before the response was dropped.");
        }

        await RequireSingleClaimAsync(host.ConnectionString, host.OrganizationId, reviewCaseId, request.IdempotencyKey, proof);
    }

    private static async Task RequireClaimReplayAsync(IAPIResponse response, Guid reviewCaseId, AssignmentProof proof)
    {
        using JsonDocument document = JsonDocument.Parse(await response.TextAsync());
        JsonElement root = document.RootElement;
        JsonElement assignee = root.GetProperty("assignee");
        if (response.Status != 200
            || !string.Equals(root.GetProperty("reviewCaseId").GetString(), reviewCaseId.ToString("D"), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(root.GetProperty("operation").GetString(), "claim", StringComparison.Ordinal)
            || !string.Equals(assignee.GetProperty("actorSource").GetString(), "developmentJwt", StringComparison.Ordinal)
            || !string.Equals(assignee.GetProperty("actorId").GetString(), proof.ActorId, StringComparison.Ordinal)
            || !string.Equals(assignee.GetProperty("assignedAt").GetString(), proof.AssignedAt, StringComparison.Ordinal)
            || !string.Equals(root.GetProperty("recordedAt").GetString(), proof.RecordedAt, StringComparison.Ordinal))
        {
            throw new E2ECommitUnprovenException("The review case claim replay was not HTTP 200 for the original assignment.");
        }
    }

    private static async Task<bool> HasSingleClaimAsync(string connectionString, Guid organizationId, Guid reviewCaseId)
    {
        OrganizationId tenant = new(organizationId);
        await using ReconciliationDbContext context = E2EDatabase.CreateContext(connectionString);
        int entries = await context.ReviewCaseAssignmentEntries.CountAsync(item => item.OrganizationId == tenant);
        ReviewCase reviewCase = await context.ReviewCases.SingleAsync(item =>
            item.OrganizationId == tenant && item.Id == new ReviewCaseId(reviewCaseId));
        return entries == 1
            && reviewCase.AssignedActorSource == ReviewCaseActorSource.DevelopmentJwt
            && string.Equals(reviewCase.AssignedActorId, E2EAccessToken.ActorId, StringComparison.Ordinal);
    }

    private static async Task RequireSingleClaimAsync(
        string connectionString,
        Guid organizationId,
        Guid reviewCaseId,
        string idempotencyKey,
        AssignmentProof proof)
    {
        OrganizationId tenant = new(organizationId);
        await using ReconciliationDbContext context = E2EDatabase.CreateContext(connectionString);
        ReviewCaseAssignmentEntry entry = await context.ReviewCaseAssignmentEntries.SingleAsync(item => item.OrganizationId == tenant);
        ReviewCase reviewCase = await context.ReviewCases.SingleAsync(item =>
            item.OrganizationId == tenant && item.Id == new ReviewCaseId(reviewCaseId));
        Assert.Equal(reviewCaseId, entry.ReviewCaseId.Value);
        Assert.Equal(ReviewCaseAssignmentOperation.Claim, entry.Operation);
        Assert.Equal(ReviewCaseActorSource.DevelopmentJwt, entry.ActorSource);
        Assert.Equal(proof.ActorId, entry.ActorId);
        Assert.Equal(Guid.Parse(idempotencyKey), entry.IdempotencyKey);
        Assert.Equal(DateTimeOffset.Parse(proof.RecordedAt), entry.RecordedAt);
        Assert.Equal(DateTimeOffset.Parse(proof.AssignedAt), entry.AssignedAt);
        Assert.Equal(ReviewCaseActorSource.DevelopmentJwt, reviewCase.AssignedActorSource);
        Assert.Equal(proof.ActorId, reviewCase.AssignedActorId);
        Assert.Equal(DateTimeOffset.Parse(proof.AssignedAt), reviewCase.AssignedAt);
        Assert.Equal(ReviewCaseStatus.Open, reviewCase.Status);
    }

    private static async Task<Guid> SeedCaseAsync(string connectionString, Guid organizationId)
    {
        OrganizationId tenant = new(organizationId);
        CurrencyCode pln = new("PLN");
        Invoice invoice = new(InvoiceId.New(), tenant, "E2E-REVIEW-ASSIGNMENT", new Money(10m, pln));
        BankTransaction bank = new(
            BankTransactionId.New(),
            tenant,
            new Money(10m, pln),
            new DateOnly(2026, 11, 6),
            "E2E-REVIEW-ASSIGNMENT");
        ReviewCase reviewCase = ReviewCase.Open(
            ReviewCaseId.New(),
            tenant,
            ReviewCaseReason.CompetingMatch,
            invoice.Id,
            bank.Id,
            sourceLabel: null,
            note: "assignment seed",
            openedAt: new DateTimeOffset(2026, 11, 6, 8, 0, 0, TimeSpan.Zero));
        await using ReconciliationDbContext context = E2EDatabase.CreateContext(connectionString);
        context.AddRange(invoice, bank, reviewCase);
        await context.SaveChangesAsync();
        return reviewCase.Id.Value;
    }

    private sealed class AssignmentProof
    {
        public string ActorId { get; set; } = string.Empty;

        public string AssignedAt { get; set; } = string.Empty;

        public string RecordedAt { get; set; } = string.Empty;
    }
}