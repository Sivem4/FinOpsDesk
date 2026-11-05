using System.Net;
using System.Text.Json;

using FinOpsDesk.IntegrationTests.Reconciliation.Persistence;
using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;

namespace FinOpsDesk.IntegrationTests.Api;

[Collection(SqlServerCollection.Name)]
public sealed class ReviewCaseAuditEndpointTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);

    private readonly SqlServerFixture _fixture;
    private readonly FinOpsDeskApiFactory _factory;

    public ReviewCaseAuditEndpointTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
        _factory = new FinOpsDeskApiFactory(fixture.ConnectionString);
    }

    [Fact]
    public async Task Stored_actions_keep_their_payload_and_leave_the_case_unchanged()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice competingInvoice = PersistenceTestData.CreateInvoice(organizationId, 11m, "INV-COMPETE");
        BankTransaction competingBank = PersistenceTestData.CreateBankTransaction(
            organizationId,
            11m,
            reference: "BANK-COMPETE");
        Invoice resolvedInvoice = PersistenceTestData.CreateInvoice(organizationId, 8m, "INV-RESOLVED");
        BankTransaction resolvedBank = PersistenceTestData.CreateBankTransaction(
            organizationId,
            8m,
            reference: "BANK-RESOLVED");
        Invoice dismissedInvoice = PersistenceTestData.CreateInvoice(organizationId, 5m, "INV-DISMISSED");
        Invoice handledInvoice = PersistenceTestData.CreateInvoice(organizationId, 4m, "INV-HANDLED");
        Allocation allocation = new(
            AllocationId.New(),
            organizationId,
            resolvedInvoice.Id,
            resolvedBank.Id,
            new Money(8m, PersistenceTestData.Pln),
            At);
        ReviewCase opened = ReviewCase.Open(
            ReviewCaseId.New(),
            organizationId,
            ReviewCaseReason.CompetingMatch,
            competingInvoice.Id,
            competingBank.Id,
            ReviewCaseSource.Exact1,
            "compete-note",
            At);
        ReviewCase resolved = ReviewCase.Open(
            ReviewCaseId.New(),
            organizationId,
            ReviewCaseReason.NeedsInvestigation,
            resolvedInvoice.Id,
            null,
            ReviewCaseSource.Manual,
            "resolved-open-note",
            At);
        ReviewCaseAuditEntry resolvedOpened = ReviewCaseAuditEntry.ForOpened(
            ReviewCaseAuditEntryId.New(),
            resolved,
            "actor-resolved-open");
        Assert.True(resolved.Resolve(
            ReviewCaseOutcome.AllocationRecorded,
            allocation.Id,
            "recorded-note",
            At).IsSuccess);
        ReviewCase dismissed = ReviewCase.Open(
            ReviewCaseId.New(),
            organizationId,
            ReviewCaseReason.NeedsInvestigation,
            dismissedInvoice.Id,
            null,
            null,
            null,
            At);
        ReviewCaseAuditEntry dismissedOpened = ReviewCaseAuditEntry.ForOpened(
            ReviewCaseAuditEntryId.New(),
            dismissed,
            "actor-dismissed-open");
        Assert.True(dismissed.Dismiss("dismissed-note", At.AddMinutes(1)).IsSuccess);
        ReviewCase handled = ReviewCase.Open(
            ReviewCaseId.New(),
            organizationId,
            ReviewCaseReason.NeedsInvestigation,
            handledInvoice.Id,
            null,
            null,
            null,
            At);
        Assert.True(handled.Resolve(
            ReviewCaseOutcome.HandledOutside,
            null,
            null,
            At).IsSuccess);
        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            context.Invoices.AddRange(competingInvoice, resolvedInvoice, dismissedInvoice, handledInvoice);
            context.BankTransactions.AddRange(competingBank, resolvedBank);
            context.Allocations.Add(allocation);
            context.ReviewCases.AddRange(opened, resolved, dismissed, handled);
            context.ReviewCaseAuditEntries.AddRange(
                ReviewCaseAuditEntry.ForOpened(ReviewCaseAuditEntryId.New(), opened, "actor-opened"),
                resolvedOpened,
                ReviewCaseAuditEntry.ForResolved(ReviewCaseAuditEntryId.New(), resolved, "actor-resolved"),
                dismissedOpened,
                ReviewCaseAuditEntry.ForDismissed(ReviewCaseAuditEntryId.New(), dismissed, "actor-dismissed"),
                ReviewCaseAuditEntry.ForResolved(ReviewCaseAuditEntryId.New(), handled, "actor-handled"));
            await context.SaveChangesAsync();
        }

        byte[] openedVersion = await RowVersionAsync(opened.Id);
        byte[] resolvedVersion = await RowVersionAsync(resolved.Id);
        byte[] dismissedVersion = await RowVersionAsync(dismissed.Id);
        byte[] handledVersion = await RowVersionAsync(handled.Id);
        int auditCount = await AuditCountAsync(organizationId);
        using HttpClient client = Client(organizationId, subject: null);

        JsonElement openedPage = await ReadPageAsync(client, opened.Id.Value);
        JsonElement resolvedPage = await ReadPageAsync(client, resolved.Id.Value);
        JsonElement dismissedPage = await ReadPageAsync(client, dismissed.Id.Value);
        JsonElement handledPage = await ReadPageAsync(client, handled.Id.Value);

        Assert.Equal(1, openedPage.GetProperty("totalCount").GetInt32());
        JsonElement openedItem = Assert.Single(openedPage.GetProperty("items").EnumerateArray());
        AssertEntry(
            openedItem,
            "Opened",
            "actor-opened",
            At,
            "CompetingMatch",
            competingInvoice.Id.Value,
            competingBank.Id.Value,
            "exact-1",
            "compete-note",
            null,
            null,
            null);
        Assert.Equal(2, resolvedPage.GetProperty("totalCount").GetInt32());
        JsonElement[] resolvedItems = resolvedPage.GetProperty("items").EnumerateArray().ToArray();
        AssertEntry(
            resolvedItems[0],
            "Opened",
            "actor-resolved-open",
            At,
            "NeedsInvestigation",
            resolvedInvoice.Id.Value,
            null,
            "manual",
            "resolved-open-note",
            null,
            null,
            null);
        AssertEntry(
            resolvedItems[1],
            "Resolved",
            "actor-resolved",
            At,
            null,
            null,
            null,
            null,
            null,
            "AllocationRecorded",
            allocation.Id.Value,
            "recorded-note");
        Assert.Equal(2, dismissedPage.GetProperty("totalCount").GetInt32());
        JsonElement[] dismissedItems = dismissedPage.GetProperty("items").EnumerateArray().ToArray();
        AssertEntry(
            dismissedItems[0],
            "Opened",
            "actor-dismissed-open",
            At,
            "NeedsInvestigation",
            dismissedInvoice.Id.Value,
            null,
            null,
            null,
            null,
            null,
            null);
        AssertEntry(
            dismissedItems[1],
            "Dismissed",
            "actor-dismissed",
            At.AddMinutes(1),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            "dismissed-note");
        JsonElement handledItem = Assert.Single(handledPage.GetProperty("items").EnumerateArray());
        AssertEntry(
            handledItem,
            "Resolved",
            "actor-handled",
            At,
            null,
            null,
            null,
            null,
            null,
            "HandledOutside",
            null,
            null);
        Assert.Equal(openedVersion, await RowVersionAsync(opened.Id));
        Assert.Equal(resolvedVersion, await RowVersionAsync(resolved.Id));
        Assert.Equal(dismissedVersion, await RowVersionAsync(dismissed.Id));
        Assert.Equal(handledVersion, await RowVersionAsync(handled.Id));
        Assert.Equal(auditCount, await AuditCountAsync(organizationId));
        Assert.Equal(ReviewCaseStatus.Open, await StatusAsync(opened.Id));
        Assert.Equal(ReviewCaseStatus.Resolved, await StatusAsync(resolved.Id));
        Assert.Equal(ReviewCaseStatus.Dismissed, await StatusAsync(dismissed.Id));
    }

    [Fact]
    public async Task Equal_timestamps_page_opened_before_the_terminal_action()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 4m, "INV-ORDER");
        ReviewCase reviewCase = ReviewCase.Open(
            ReviewCaseId.New(),
            organizationId,
            ReviewCaseReason.NeedsInvestigation,
            invoice.Id,
            null,
            ReviewCaseSource.Smart1,
            null,
            At);
        ReviewCaseAuditEntry openedEntry = ReviewCaseAuditEntry.ForOpened(
            new ReviewCaseAuditEntryId(Guid.Parse("00000000-0000-0000-0000-000000000002")),
            reviewCase,
            "actor-open");
        Assert.True(reviewCase.Resolve(
            ReviewCaseOutcome.NoActionRequired,
            null,
            null,
            At).IsSuccess);
        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            context.Invoices.Add(invoice);
            context.ReviewCases.Add(reviewCase);
            context.ReviewCaseAuditEntries.AddRange(
                openedEntry,
                ReviewCaseAuditEntry.ForResolved(
                    new ReviewCaseAuditEntryId(Guid.Parse("00000000-0000-0000-0000-000000000001")),
                    reviewCase,
                    "actor-close"));
            await context.SaveChangesAsync();
        }

        using HttpClient client = Client(organizationId, "reader");

        JsonElement first = await ReadPageAsync(client, reviewCase.Id.Value, "?page=1&pageSize=1");
        JsonElement second = await ReadPageAsync(client, reviewCase.Id.Value, "?page=2&pageSize=1");
        JsonElement beyond = await ReadPageAsync(client, reviewCase.Id.Value, "?page=3&pageSize=1");

        Assert.Equal(2, first.GetProperty("totalCount").GetInt32());
        Assert.Equal("Opened", Assert.Single(first.GetProperty("items").EnumerateArray()).GetProperty("action").GetString());
        Assert.Equal(2, second.GetProperty("totalCount").GetInt32());
        JsonElement closed = Assert.Single(second.GetProperty("items").EnumerateArray());
        Assert.Equal("Resolved", closed.GetProperty("action").GetString());
        Assert.Equal("NoActionRequired", closed.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, closed.GetProperty("reason").ValueKind);
        Assert.Equal(JsonValueKind.Null, closed.GetProperty("invoiceId").ValueKind);
        Assert.Equal(2, beyond.GetProperty("totalCount").GetInt32());
        Assert.Empty(beyond.GetProperty("items").EnumerateArray());
        Assert.Equal(3, beyond.GetProperty("page").GetInt32());
        Assert.Equal(1, beyond.GetProperty("pageSize").GetInt32());
    }

    [Fact]
    public async Task Historical_cases_return_an_empty_page_or_only_the_terminal_entry()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice emptyInvoice = PersistenceTestData.CreateInvoice(organizationId, 3m, "INV-EMPTY");
        Invoice closedInvoice = PersistenceTestData.CreateInvoice(organizationId, 2m, "INV-HIST");
        ReviewCase empty = ReviewCase.Open(
            ReviewCaseId.New(),
            organizationId,
            ReviewCaseReason.NeedsInvestigation,
            emptyInvoice.Id,
            null,
            null,
            "still-open",
            At);
        ReviewCase closed = ReviewCase.Open(
            ReviewCaseId.New(),
            organizationId,
            ReviewCaseReason.NeedsInvestigation,
            closedInvoice.Id,
            null,
            ReviewCaseSource.Manual,
            "historical-open-note",
            At);
        Assert.True(closed.Dismiss(null, At.AddMinutes(2)).IsSuccess);
        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            context.Invoices.AddRange(emptyInvoice, closedInvoice);
            context.ReviewCases.AddRange(empty, closed);
            context.ReviewCaseAuditEntries.Add(
                ReviewCaseAuditEntry.ForDismissed(ReviewCaseAuditEntryId.New(), closed, "actor-historical"));
            await context.SaveChangesAsync();
        }

        using HttpClient client = Client(organizationId, subject: null);

        JsonElement emptyPage = await ReadPageAsync(client, empty.Id.Value);
        JsonElement closedPage = await ReadPageAsync(client, closed.Id.Value);
        JsonElement detail = await ReadJsonAsync(client, "/api/review-cases/" + closed.Id.Value.ToString("D"));

        Assert.Equal(0, emptyPage.GetProperty("totalCount").GetInt32());
        Assert.Empty(emptyPage.GetProperty("items").EnumerateArray());
        Assert.Equal(1, emptyPage.GetProperty("page").GetInt32());
        Assert.Equal(50, emptyPage.GetProperty("pageSize").GetInt32());
        JsonElement onlyClose = Assert.Single(closedPage.GetProperty("items").EnumerateArray());
        Assert.Equal("Dismissed", onlyClose.GetProperty("action").GetString());
        Assert.Equal(JsonValueKind.Null, onlyClose.GetProperty("reason").ValueKind);
        Assert.Equal(JsonValueKind.Null, onlyClose.GetProperty("note").ValueKind);
        Assert.Equal("NeedsInvestigation", detail.GetProperty("reviewCase").GetProperty("reason").GetString());
        Assert.Equal("historical-open-note", detail.GetProperty("reviewCase").GetProperty("note").GetString());
        Assert.DoesNotContain("actor-historical", detail.GetRawText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Malformed_review_case_id_returns_400(string reviewCaseId)
    {
        using HttpClient client = Client(OrganizationId.New(), "reader");

        HttpResponseMessage response = await client.GetAsync("/api/review-cases/" + reviewCaseId + "/audit");

        await AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            ReconciliationApplicationErrors.ReviewCaseAuditInvalidRequest.Code);
    }

    [Theory]
    [InlineData("?organizationId=11111111-1111-1111-1111-111111111111")]
    [InlineData("?page=1&actorId=spoof")]
    public async Task Unsupported_query_parameter_returns_400(string query)
    {
        OrganizationId organizationId = OrganizationId.New();
        ReviewCaseId reviewCaseId = await SaveEmptyCaseAsync(organizationId, "INV-QUERY");
        using HttpClient client = Client(organizationId, subject: null);

        HttpResponseMessage response = await client.GetAsync(AuditPath(reviewCaseId.Value) + query);

        await AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            ReconciliationApplicationErrors.ReviewCaseAuditInvalidRequest.Code);
        Assert.Equal(0, await AuditCountAsync(organizationId));
    }

    [Theory]
    [InlineData("?page=0")]
    [InlineData("?page=-1")]
    [InlineData("?pageSize=0")]
    [InlineData("?pageSize=101")]
    [InlineData("?page=abc")]
    [InlineData("?page=1&page=2")]
    [InlineData("?pageSize=1&pageSize=2")]
    [InlineData("?pageSize=1&PageSize=2")]
    [InlineData("?Page=0")]
    [InlineData("?PageSize=101")]
    [InlineData("?page=2147483647&pageSize=100")]
    public async Task Invalid_pagination_returns_400(string query)
    {
        OrganizationId organizationId = OrganizationId.New();
        ReviewCaseId reviewCaseId = await SaveEmptyCaseAsync(organizationId, "INV-PAGE");
        using HttpClient client = Client(organizationId, subject: null);

        HttpResponseMessage response = await client.GetAsync(AuditPath(reviewCaseId.Value) + query);

        await AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            ReconciliationApplicationErrors.ReviewCaseAuditInvalidPagination.Code);
    }

    [Fact]
    public async Task Foreign_and_unknown_cases_are_the_same_not_found_and_hide_the_other_history()
    {
        OrganizationId owner = OrganizationId.New();
        OrganizationId other = OrganizationId.New();
        Invoice ownerInvoice = PersistenceTestData.CreateInvoice(owner, 6m, "INV-OWNER");
        Invoice otherInvoice = PersistenceTestData.CreateInvoice(other, 7m, "INV-OTHER");
        ReviewCase ownerCase = ReviewCase.Open(
            ReviewCaseId.New(),
            owner,
            ReviewCaseReason.NeedsInvestigation,
            ownerInvoice.Id,
            null,
            ReviewCaseSource.Manual,
            "owner-note-only",
            At);
        ReviewCase otherCase = ReviewCase.Open(
            ReviewCaseId.New(),
            other,
            ReviewCaseReason.NeedsInvestigation,
            otherInvoice.Id,
            null,
            ReviewCaseSource.Exact1,
            "other-note-only",
            At);
        ReviewCaseAuditEntryId ownerEntryId = ReviewCaseAuditEntryId.New();
        ReviewCaseAuditEntryId otherEntryId = ReviewCaseAuditEntryId.New();
        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            context.Invoices.AddRange(ownerInvoice, otherInvoice);
            context.ReviewCases.AddRange(ownerCase, otherCase);
            context.ReviewCaseAuditEntries.AddRange(
                ReviewCaseAuditEntry.ForOpened(ownerEntryId, ownerCase, "actor-owner-only"),
                ReviewCaseAuditEntry.ForOpened(otherEntryId, otherCase, "actor-other-only"));
            await context.SaveChangesAsync();
        }

        using HttpClient ownerClient = Client(owner, subject: null);
        using HttpClient otherClient = Client(other, "other-reader");

        JsonElement ownPage = await ReadPageAsync(ownerClient, ownerCase.Id.Value);
        HttpResponseMessage foreign = await ownerClient.GetAsync(AuditPath(otherCase.Id.Value));
        HttpResponseMessage unknown = await ownerClient.GetAsync(AuditPath(Guid.NewGuid()));
        string foreignBody = await foreign.Content.ReadAsStringAsync();
        string unknownBody = await unknown.Content.ReadAsStringAsync();
        JsonElement otherOwnPage = await ReadPageAsync(otherClient, otherCase.Id.Value);

        Assert.Equal("actor-owner-only", Assert.Single(ownPage.GetProperty("items").EnumerateArray()).GetProperty("actorId").GetString());
        Assert.DoesNotContain("actor-other-only", ownPage.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("other-note-only", ownPage.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(otherEntryId.Value.ToString(), ownPage.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(other.Value.ToString(), ownPage.GetRawText(), StringComparison.OrdinalIgnoreCase);
        await AssertProblemBodyAsync(foreign, foreignBody);
        await AssertProblemBodyAsync(unknown, unknownBody);
        Assert.Equal(
            ReconciliationApplicationErrors.ReviewCaseNotFound.Code,
            JsonDocument.Parse(foreignBody).RootElement.GetProperty("code").GetString());
        Assert.Equal(
            JsonDocument.Parse(unknownBody).RootElement.GetProperty("code").GetString(),
            JsonDocument.Parse(foreignBody).RootElement.GetProperty("code").GetString());
        Assert.Equal(
            JsonDocument.Parse(unknownBody).RootElement.GetProperty("title").GetString(),
            JsonDocument.Parse(foreignBody).RootElement.GetProperty("title").GetString());
        AssertHidden(foreignBody);
        AssertHidden(unknownBody);
        Assert.Equal("actor-other-only", Assert.Single(otherOwnPage.GetProperty("items").EnumerateArray()).GetProperty("actorId").GetString());
        Assert.DoesNotContain("actor-owner-only", otherOwnPage.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(ownerEntryId.Value.ToString(), otherOwnPage.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    private HttpClient Client(OrganizationId organizationId, string? subject)
    {
        return _factory.CreateApiClient(TestAccessTokens.CreateForActor(organizationId.Value, subject));
    }

    private async Task<ReviewCaseId> SaveEmptyCaseAsync(OrganizationId organizationId, string invoiceNumber)
    {
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 1m, invoiceNumber);
        ReviewCase reviewCase = ReviewCase.Open(
            ReviewCaseId.New(),
            organizationId,
            ReviewCaseReason.NeedsInvestigation,
            invoice.Id,
            null,
            null,
            null,
            At);
        await using ReconciliationDbContext context = _fixture.CreateContext();
        context.Invoices.Add(invoice);
        context.ReviewCases.Add(reviewCase);
        await context.SaveChangesAsync();
        return reviewCase.Id;
    }

    private async Task<byte[]> RowVersionAsync(ReviewCaseId reviewCaseId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        return await context.ReviewCases
            .AsNoTracking()
            .Where(reviewCase => reviewCase.Id == reviewCaseId)
            .Select(reviewCase => EF.Property<byte[]>(reviewCase, "RowVersion"))
            .SingleAsync();
    }

    private async Task<ReviewCaseStatus> StatusAsync(ReviewCaseId reviewCaseId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        return await context.ReviewCases
            .AsNoTracking()
            .Where(reviewCase => reviewCase.Id == reviewCaseId)
            .Select(reviewCase => reviewCase.Status)
            .SingleAsync();
    }

    private async Task<int> AuditCountAsync(OrganizationId organizationId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        return await context.ReviewCaseAuditEntries.CountAsync(entry => entry.OrganizationId == organizationId);
    }

    private static async Task<JsonElement> ReadPageAsync(HttpClient client, Guid reviewCaseId, string query = "")
    {
        return await ReadJsonAsync(client, AuditPath(reviewCaseId) + query);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpClient client, string path)
    {
        HttpResponseMessage response = await client.GetAsync(path);
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        string body = await response.Content.ReadAsStringAsync();
        Assert.Equal(status, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(body);
        Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
    }

    private static Task AssertProblemBodyAsync(HttpResponseMessage response, string body)
    {
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(body);
        Assert.Equal(
            ReconciliationApplicationErrors.ReviewCaseNotFound.Code,
            document.RootElement.GetProperty("code").GetString());
        return Task.CompletedTask;
    }

    private static void AssertHidden(string body)
    {
        Assert.DoesNotContain("actor-owner-only", body, StringComparison.Ordinal);
        Assert.DoesNotContain("actor-other-only", body, StringComparison.Ordinal);
        Assert.DoesNotContain("owner-note-only", body, StringComparison.Ordinal);
        Assert.DoesNotContain("other-note-only", body, StringComparison.Ordinal);
        Assert.DoesNotContain("INV-OWNER", body, StringComparison.Ordinal);
        Assert.DoesNotContain("INV-OTHER", body, StringComparison.Ordinal);
    }

    private static void AssertEntry(
        JsonElement item,
        string action,
        string actorId,
        DateTimeOffset recordedAt,
        string? reason,
        Guid? invoiceId,
        Guid? bankTransactionId,
        string? sourceLabel,
        string? note,
        string? outcome,
        Guid? allocationId,
        string? resolutionNote)
    {
        Assert.Equal(action, item.GetProperty("action").GetString());
        Assert.Equal(actorId, item.GetProperty("actorId").GetString());
        Assert.Equal(recordedAt, item.GetProperty("recordedAt").GetDateTimeOffset());
        AssertOptional(item, "reason", reason);
        AssertOptionalGuid(item, "invoiceId", invoiceId);
        AssertOptionalGuid(item, "bankTransactionId", bankTransactionId);
        AssertOptional(item, "sourceLabel", sourceLabel);
        AssertOptional(item, "note", note);
        AssertOptional(item, "outcome", outcome);
        AssertOptionalGuid(item, "allocationId", allocationId);
        AssertOptional(item, "resolutionNote", resolutionNote);
        Assert.False(item.TryGetProperty("organizationId", out _));
        Assert.False(item.TryGetProperty("reviewCaseId", out _));
        Assert.False(item.TryGetProperty("concurrencyToken", out _));
    }

    private static void AssertOptional(JsonElement item, string name, string? expected)
    {
        JsonElement value = item.GetProperty(name);
        if (expected is null)
        {
            Assert.Equal(JsonValueKind.Null, value.ValueKind);
            return;
        }

        Assert.Equal(expected, value.GetString());
    }

    private static void AssertOptionalGuid(JsonElement item, string name, Guid? expected)
    {
        JsonElement value = item.GetProperty(name);
        if (expected is null)
        {
            Assert.Equal(JsonValueKind.Null, value.ValueKind);
            return;
        }

        Assert.Equal(expected.Value, value.GetGuid());
    }

    private static string AuditPath(Guid reviewCaseId)
    {
        return "/api/review-cases/" + reviewCaseId.ToString("D") + "/audit";
    }
}