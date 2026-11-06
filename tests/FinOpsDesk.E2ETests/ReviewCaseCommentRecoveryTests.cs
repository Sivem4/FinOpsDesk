using System.Text.Json;
using System.Text.RegularExpressions;

using FinOpsDesk.Modules.Reconciliation.Application.ReviewCases;
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
public sealed class ReviewCaseCommentRecoveryTests
{
    private const string AttemptKey = "finopsdesk.review-case-comment-attempt";
    private const string OpenAttemptKey = "finopsdesk.review-case-open-attempt";
    private const string DecisionAttemptKey = "finopsdesk.review-case-decision-attempt";
    private const string CommentText = "desk comment";

    private static readonly Regex CommentUrl = new(
        @"/api/review-cases/[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}/comments$",
        RegexOptions.None);

    [Fact]
    public async Task Committed_review_case_comment_survives_a_lost_response_and_replays()
    {
        await E2ETestRun.RunAsync(TimeSpan.FromSeconds(240), async host =>
        {
            Guid reviewCaseId = await SeedCaseAsync(host.ConnectionString, host.OrganizationId);
            CaseSnapshot before = await ReadCaseAsync(host.ConnectionString, host.OrganizationId, reviewCaseId);
            CommentProof proof = new();

            await host.SignInAsync();
            await host.Page.GetByRole(AriaRole.Link, new() { Name = "Review cases", Exact = true }).ClickAsync();
            await host.Page.GetByText("comment seed").WaitForAsync();
            await host.Page.GetByRole(AriaRole.Button, new() { Name = "Show review case", Exact = false }).ClickAsync();
            await host.Page.GetByRole(AriaRole.Textbox, new() { Name = "Comment", Exact = true }).FillAsync(CommentText);

            E2ECapturedRequest captured;
            await using (E2ELossSession loss = await E2ELossSession.ArmAsync(
                host.Page,
                CommentUrl,
                () => HasSingleCommentAsync(host.ConnectionString, host.OrganizationId),
                (request, response) => ProveCommentCommitAsync(host, request, response, reviewCaseId, before, proof)))
            {
                await host.Page.GetByRole(AriaRole.Button, new() { Name = "Add comment", Exact = true }).ClickAsync();
                captured = await loss.WaitAsync();
                Assert.Equal(1, loss.PostCount);
                Assert.Equal(0, loss.UnexpectedPosts);
                await Assertions.Expect(host.Page.GetByText(
                    "This comment has no verified result. Sending it again uses the same request.")).ToBeVisibleAsync();
                await Assertions.Expect(host.Page.GetByText("Comment saved.")).ToHaveCountAsync(0);
                await E2EBrowser.RequireStoredRequestAsync(host.Page, AttemptKey, captured);
            }

            await using (E2EPostWatch watch = await E2ERecovery.WatchReloadAndSignInAsync(host, CommentUrl))
            {
                await Assertions.Expect(host.Page.GetByRole(AriaRole.Button, new() { Name = "Send the same comment again" })).ToBeVisibleAsync();
                await E2EBrowser.RequireStoredRequestAsync(host.Page, AttemptKey, captured);
                Assert.Equal(0, watch.Count);
            }

            await E2ELostResponse.ReplayAsync(
                host.Page,
                CommentUrl,
                captured,
                () => host.Page.GetByRole(AriaRole.Button, new() { Name = "Send the same comment again" }).ClickAsync(),
                response => RequireCommentReplayAsync(response, proof));
            await Assertions.Expect(host.Page.GetByText("This comment was already stored.")).ToBeVisibleAsync();
            await Assertions.Expect(host.Page.GetByText("Stored actor " + proof.ActorId)).ToBeVisibleAsync();
            await Assertions.Expect(host.Page.GetByText("Recorded " + proof.RecordedAt)).ToBeVisibleAsync();
            await RequireSingleCommentAsync(
                host.ConnectionString,
                host.OrganizationId,
                reviewCaseId,
                captured.IdempotencyKey,
                proof);
            CaseSnapshot after = await ReadCaseAsync(host.ConnectionString, host.OrganizationId, reviewCaseId);
            Assert.Equal(before, after);
            Assert.Null(await E2EBrowser.ReadSessionAsync(host.Page, AttemptKey));
            Assert.Null(await E2EBrowser.ReadSessionAsync(host.Page, OpenAttemptKey));
            Assert.Null(await E2EBrowser.ReadSessionAsync(host.Page, DecisionAttemptKey));
            host.AssertBrowserIsQuiet(allowAbortedRequests: true);
        });
    }

    private static async Task ProveCommentCommitAsync(
        E2EHost host,
        E2ECapturedRequest request,
        IAPIResponse response,
        Guid reviewCaseId,
        CaseSnapshot before,
        CommentProof proof)
    {
        E2EBrowser.RequireNoOrganizationId(request, host.OrganizationId);
        using JsonDocument document = JsonDocument.Parse(await response.TextAsync());
        JsonElement root = document.RootElement;
        string? id = root.GetProperty("id").GetString();
        string? text = root.GetProperty("text").GetString();
        string? actorId = root.GetProperty("actorId").GetString();
        string? recordedAt = root.GetProperty("recordedAt").GetString();
        bool identity = response.Status == 201
            && Guid.TryParse(id, out Guid parsed)
            && parsed != Guid.Empty
            && string.Equals(text, CommentText, StringComparison.Ordinal)
            && string.Equals(actorId, E2EAccessToken.ActorId, StringComparison.Ordinal)
            && recordedAt is not null;
        proof.Id = id ?? string.Empty;
        proof.ActorId = actorId ?? string.Empty;
        proof.RecordedAt = recordedAt ?? string.Empty;
        bool committed = await HasSingleCommentAsync(host.ConnectionString, host.OrganizationId);
        CaseSnapshot during = await ReadCaseAsync(host.ConnectionString, host.OrganizationId, reviewCaseId);
        if (!identity || !committed || during != before)
        {
            throw new E2ECommitUnprovenException(
                "The review case comment was not a committed HTTP 201 before the response was dropped.");
        }

        await RequireSingleCommentAsync(host.ConnectionString, host.OrganizationId, reviewCaseId, request.IdempotencyKey, proof);
    }

    private static async Task RequireCommentReplayAsync(IAPIResponse response, CommentProof proof)
    {
        using JsonDocument document = JsonDocument.Parse(await response.TextAsync());
        JsonElement root = document.RootElement;
        if (response.Status != 200
            || !string.Equals(root.GetProperty("id").GetString(), proof.Id, StringComparison.Ordinal)
            || !string.Equals(root.GetProperty("text").GetString(), CommentText, StringComparison.Ordinal)
            || !string.Equals(root.GetProperty("actorId").GetString(), proof.ActorId, StringComparison.Ordinal)
            || !string.Equals(root.GetProperty("recordedAt").GetString(), proof.RecordedAt, StringComparison.Ordinal))
        {
            throw new E2ECommitUnprovenException("The review case comment replay was not HTTP 200 for the original comment.");
        }
    }

    private static async Task<bool> HasSingleCommentAsync(string connectionString, Guid organizationId)
    {
        OrganizationId tenant = new(organizationId);
        await using ReconciliationDbContext context = E2EDatabase.CreateContext(connectionString);
        int comments = await context.ReviewCaseComments.CountAsync(item => item.OrganizationId == tenant);
        int cases = await context.ReviewCases.CountAsync(item => item.OrganizationId == tenant);
        return comments == 1 && cases == 1;
    }

    private static async Task RequireSingleCommentAsync(
        string connectionString,
        Guid organizationId,
        Guid reviewCaseId,
        string idempotencyKey,
        CommentProof proof)
    {
        OrganizationId tenant = new(organizationId);
        await using ReconciliationDbContext context = E2EDatabase.CreateContext(connectionString);
        ReviewCaseComment comment = await context.ReviewCaseComments.SingleAsync(item => item.OrganizationId == tenant);
        Assert.Equal(Guid.Parse(proof.Id), comment.Id.Value);
        Assert.Equal(reviewCaseId, comment.ReviewCaseId.Value);
        Assert.Equal(CommentText, comment.Text);
        Assert.Equal(proof.ActorId, comment.ActorId);
        Assert.Equal(DateTimeOffset.Parse(proof.RecordedAt), comment.RecordedAt);
        Assert.Equal(Guid.Parse(idempotencyKey), comment.IdempotencyKey);
        Assert.Equal(0, await context.ReviewCaseAuditEntries.CountAsync(item => item.OrganizationId == tenant));
    }

    private static async Task<CaseSnapshot> ReadCaseAsync(string connectionString, Guid organizationId, Guid reviewCaseId)
    {
        OrganizationId tenant = new(organizationId);
        await using ReconciliationDbContext context = E2EDatabase.CreateContext(connectionString);
        ReviewCase reviewCase = await context.ReviewCases.SingleAsync(item =>
            item.OrganizationId == tenant && item.Id == new ReviewCaseId(reviewCaseId));
        int auditCount = await context.ReviewCaseAuditEntries.CountAsync(item => item.OrganizationId == tenant);
        string token = ReviewCaseConcurrencyToken.Encode(
            context.Entry(reviewCase).Property<byte[]>("RowVersion").CurrentValue);
        return new CaseSnapshot(reviewCase.Status, reviewCase.Outcome, reviewCase.ClosedAt, token, auditCount);
    }

    private static async Task<Guid> SeedCaseAsync(string connectionString, Guid organizationId)
    {
        OrganizationId tenant = new(organizationId);
        CurrencyCode pln = new("PLN");
        Invoice invoice = new(InvoiceId.New(), tenant, "E2E-REVIEW-COMMENT", new Money(10m, pln));
        BankTransaction bank = new(
            BankTransactionId.New(),
            tenant,
            new Money(10m, pln),
            new DateOnly(2026, 11, 6),
            "E2E-REVIEW-COMMENT");
        ReviewCase reviewCase = ReviewCase.Open(
            ReviewCaseId.New(),
            tenant,
            ReviewCaseReason.CompetingMatch,
            invoice.Id,
            bank.Id,
            sourceLabel: null,
            note: "comment seed",
            openedAt: new DateTimeOffset(2026, 11, 6, 8, 0, 0, TimeSpan.Zero));
        await using ReconciliationDbContext context = E2EDatabase.CreateContext(connectionString);
        context.AddRange(invoice, bank, reviewCase);
        await context.SaveChangesAsync();
        return reviewCase.Id.Value;
    }

    private sealed class CommentProof
    {
        public string Id { get; set; } = string.Empty;

        public string ActorId { get; set; } = string.Empty;

        public string RecordedAt { get; set; } = string.Empty;
    }

    private sealed record CaseSnapshot(
        ReviewCaseStatus Status,
        ReviewCaseOutcome? Outcome,
        DateTimeOffset? ClosedAt,
        string Token,
        int AuditCount);
}