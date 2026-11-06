using System.Data.Common;
using System.Net;
using System.Text.Json;

using FinOpsDesk.IntegrationTests.Reconciliation.Persistence;
using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace FinOpsDesk.IntegrationTests.Api;

[Collection(SqlServerCollection.Name)]
public sealed class ReviewCaseSummaryEndpointTests : IDisposable
{
    private static readonly DateTimeOffset OpenedAt = new(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset ClosedAt = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly SqlServerFixture _fixture;
    private readonly FinOpsDeskApiFactory _factory;

    public ReviewCaseSummaryEndpointTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
        _factory = new FinOpsDeskApiFactory(fixture.ConnectionString);
    }

    [Fact]
    public async Task Empty_organization_returns_six_zeros_from_one_aggregate_command()
    {
        SummarySqlCapture capture = new();
        using FinOpsDeskApiFactory factory = CreateCapturingFactory(capture);
        OrganizationId organizationId = OrganizationId.New();
        using HttpClient client = factory.CreateApiClient(TestAccessTokens.Create(organizationId.Value));

        capture.Commands.Clear();
        ReviewCaseSummaryResponse summary = await ReadSummaryAsync(client);

        AssertZeros(summary);
        RequireAggregateCommand(capture, organizationId);
    }

    [Fact]
    public async Task Summary_counts_every_status_and_reason_including_both_investigation_shapes()
    {
        OrganizationId organizationId = OrganizationId.New();
        await SeedCombinationsAsync(organizationId);
        using HttpClient client = Client(organizationId);

        ReviewCaseSummaryResponse summary = await ReadSummaryAsync(client);

        Assert.Equal(7, summary.TotalCount);
        Assert.Equal(3, summary.OpenCount);
        Assert.Equal(2, summary.ResolvedCount);
        Assert.Equal(2, summary.DismissedCount);
        Assert.Equal(1, summary.OpenCompetingMatchCount);
        Assert.Equal(2, summary.OpenNeedsInvestigationCount);
        AssertIdentities(summary);
    }

    [Fact]
    public async Task Summary_counts_more_cases_than_one_list_page_and_matches_the_unfiltered_total()
    {
        OrganizationId organizationId = OrganizationId.New();
        await SeedOpenInvestigationsAsync(organizationId, 101);
        using HttpClient client = Client(organizationId);

        HttpResponseMessage list = await client.GetAsync("/api/review-cases?page=1&pageSize=50");
        string listBody = await list.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using JsonDocument listDocument = JsonDocument.Parse(listBody);
        Assert.Equal(50, listDocument.RootElement.GetProperty("items").GetArrayLength());
        Assert.Equal(101, listDocument.RootElement.GetProperty("totalCount").GetInt32());

        ReviewCaseSummaryResponse summary = await ReadSummaryAsync(client);
        Assert.Equal(101, summary.TotalCount);
        Assert.Equal(101, summary.OpenCount);
        Assert.Equal(0, summary.ResolvedCount);
        Assert.Equal(0, summary.DismissedCount);
        Assert.Equal(0, summary.OpenCompetingMatchCount);
        Assert.Equal(101, summary.OpenNeedsInvestigationCount);
        Assert.Equal(listDocument.RootElement.GetProperty("totalCount").GetInt32(), summary.TotalCount);
    }

    [Fact]
    public async Task Later_case_for_the_same_subject_counts_separately()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 40m, "SUMMARY-SAME");
        BankTransaction bank = PersistenceTestData.CreateBankTransaction(organizationId, 40m, reference: "SUMMARY-SAME");
        ReviewCase closed = OpenCompeting(organizationId, invoice, bank, OpenedAt);
        Assert.True(closed.Resolve(ReviewCaseOutcome.NoActionRequired, null, null, ClosedAt).IsSuccess);
        ReviewCase open = OpenCompeting(organizationId, invoice, bank, ClosedAt);
        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            context.AddRange(invoice, bank, closed, open);
            await context.SaveChangesAsync();
        }

        using HttpClient client = Client(organizationId);
        ReviewCaseSummaryResponse summary = await ReadSummaryAsync(client);

        Assert.Equal(2, summary.TotalCount);
        Assert.Equal(1, summary.OpenCount);
        Assert.Equal(1, summary.ResolvedCount);
        Assert.Equal(0, summary.DismissedCount);
        Assert.Equal(1, summary.OpenCompetingMatchCount);
        Assert.Equal(0, summary.OpenNeedsInvestigationCount);
        AssertIdentities(summary);
    }

    [Fact]
    public async Task Summary_is_limited_to_the_authenticated_organization()
    {
        OrganizationId organizationA = OrganizationId.New();
        OrganizationId organizationB = OrganizationId.New();
        await SeedOpenInvestigationsAsync(organizationA, 2);
        await SeedOpenInvestigationsAsync(organizationB, 5);
        using HttpClient client = Client(organizationA);

        ReviewCaseSummaryResponse summary = await ReadSummaryAsync(client);
        Assert.Equal(2, summary.TotalCount);
        Assert.Equal(2, summary.OpenNeedsInvestigationCount);

        using HttpClient other = Client(organizationB);
        HttpResponseMessage spoofed = await other.GetAsync(
            "/api/review-cases/summary?organizationId=" + organizationA.Value.ToString("D"));
        await AssertProblemAsync(
            spoofed,
            HttpStatusCode.BadRequest,
            ReconciliationApplicationErrors.ReviewCaseInvalidRequest.Code);
        ReviewCaseSummaryResponse otherSummary = await ReadSummaryAsync(other);
        Assert.Equal(5, otherSummary.TotalCount);
    }

    [Theory]
    [InlineData("?page=1")]
    [InlineData("?pageSize=50")]
    [InlineData("?status=Open")]
    [InlineData("?invoiceId=11111111-1111-1111-1111-111111111111")]
    [InlineData("?bankTransactionId=11111111-1111-1111-1111-111111111111")]
    [InlineData("?organizationId=11111111-1111-1111-1111-111111111111")]
    [InlineData("?unknown=1")]
    [InlineData("?Page=1")]
    [InlineData("?page=1&page=2")]
    [InlineData("?page=")]
    public async Task Unsupported_query_parameters_are_rejected(string query)
    {
        using HttpClient client = Client(OrganizationId.New());

        HttpResponseMessage response = await client.GetAsync("/api/review-cases/summary" + query);

        await AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            ReconciliationApplicationErrors.ReviewCaseInvalidRequest.Code);
    }

    [Fact]
    public async Task Jwt_without_an_actor_can_read_the_summary()
    {
        OrganizationId organizationId = OrganizationId.New();
        await SeedOpenInvestigationsAsync(organizationId, 1);
        using HttpClient client = _factory.CreateApiClient(
            TestAccessTokens.CreateForActor(organizationId.Value, subject: null));

        ReviewCaseSummaryResponse summary = await ReadSummaryAsync(client);

        Assert.Equal(1, summary.TotalCount);
        Assert.Equal(1, summary.OpenCount);
    }

    [Fact]
    public async Task Read_leaves_rowversion_and_audit_unchanged()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 10m, "SUMMARY-STABLE");
        ReviewCase reviewCase = OpenInvestigation(organizationId, invoice, null, OpenedAt, "stable");
        ReviewCaseAuditEntry audit = ReviewCaseAuditEntry.ForOpened(
            ReviewCaseAuditEntryId.New(),
            reviewCase,
            "summary-reader");
        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            context.AddRange(invoice, reviewCase, audit);
            await context.SaveChangesAsync();
        }

        byte[] before = await ReadRowVersionAsync(reviewCase.Id);
        int auditBefore = await AuditCountAsync(organizationId);
        using HttpClient client = Client(organizationId);
        ReviewCaseSummaryResponse summary = await ReadSummaryAsync(client);
        byte[] after = await ReadRowVersionAsync(reviewCase.Id);

        Assert.Equal(1, summary.TotalCount);
        Assert.Equal(auditBefore, await AuditCountAsync(organizationId));
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Summary_detail_and_audit_routes_stay_distinct()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 10m, "SUMMARY-ROUTE");
        ReviewCase reviewCase = OpenInvestigation(organizationId, invoice, null, OpenedAt, "route");
        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            context.AddRange(invoice, reviewCase);
            context.ReviewCaseAuditEntries.Add(
                ReviewCaseAuditEntry.ForOpened(ReviewCaseAuditEntryId.New(), reviewCase, "route-actor"));
            await context.SaveChangesAsync();
        }

        using HttpClient client = Client(organizationId);
        ReviewCaseSummaryResponse summary = await ReadSummaryAsync(client);
        Assert.Equal(1, summary.TotalCount);
        Assert.Equal(6, CountProperties(await ReadBodyAsync(client, "/api/review-cases/summary")));

        HttpResponseMessage detail = await client.GetAsync("/api/review-cases/" + reviewCase.Id.Value.ToString("D"));
        string detailBody = await detail.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        using JsonDocument detailDocument = JsonDocument.Parse(detailBody);
        Assert.Equal(
            reviewCase.Id.Value,
            detailDocument.RootElement.GetProperty("reviewCase").GetProperty("reviewCaseId").GetGuid());
        Assert.Equal("Open", detailDocument.RootElement.GetProperty("reviewCase").GetProperty("status").GetString());

        HttpResponseMessage audit = await client.GetAsync(
            "/api/review-cases/" + reviewCase.Id.Value.ToString("D") + "/audit");
        string auditBody = await audit.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, audit.StatusCode);
        Assert.Contains("route-actor", auditBody, StringComparison.Ordinal);
        string summaryBody = await ReadBodyAsync(client, "/api/review-cases/summary");
        Assert.DoesNotContain("route-actor", summaryBody, StringComparison.Ordinal);
        Assert.DoesNotContain(reviewCase.Id.Value.ToString("D"), summaryBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Executed_summary_command_is_one_64_bit_organization_aggregate()
    {
        SummarySqlCapture capture = new();
        using FinOpsDeskApiFactory factory = CreateCapturingFactory(capture);
        OrganizationId organizationId = OrganizationId.New();
        await SeedCombinationsAsync(organizationId);
        using HttpClient client = factory.CreateApiClient(TestAccessTokens.Create(organizationId.Value));

        capture.Commands.Clear();
        ReviewCaseSummaryResponse summary = await ReadSummaryAsync(client);

        Assert.Equal(7, summary.TotalCount);
        AssertIdentities(summary);
        RequireAggregateCommand(capture, organizationId);
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    private FinOpsDeskApiFactory CreateCapturingFactory(SummarySqlCapture capture)
    {
        return new FinOpsDeskApiFactory(
            _fixture.ConnectionString,
            services =>
            {
                services.AddSingleton(capture);
                services.AddDbContext<ReconciliationDbContext>((_, options) =>
                {
                    options.AddInterceptors(capture);
                });
            });
    }

    private HttpClient Client(OrganizationId organizationId)
    {
        return _factory.CreateApiClient(TestAccessTokens.Create(organizationId.Value));
    }

    private async Task SeedCombinationsAsync(OrganizationId organizationId)
    {
        Invoice competingOpenInvoice = PersistenceTestData.CreateInvoice(organizationId, 10m, "SUM-OC");
        BankTransaction competingOpenBank = PersistenceTestData.CreateBankTransaction(organizationId, 10m, reference: "SUM-OC");
        Invoice competingResolvedInvoice = PersistenceTestData.CreateInvoice(organizationId, 11m, "SUM-RC");
        BankTransaction competingResolvedBank = PersistenceTestData.CreateBankTransaction(organizationId, 11m, reference: "SUM-RC");
        Invoice competingDismissedInvoice = PersistenceTestData.CreateInvoice(organizationId, 12m, "SUM-DC");
        BankTransaction competingDismissedBank = PersistenceTestData.CreateBankTransaction(organizationId, 12m, reference: "SUM-DC");
        Invoice openInvoice = PersistenceTestData.CreateInvoice(organizationId, 13m, "SUM-OI");
        Invoice resolvedInvoice = PersistenceTestData.CreateInvoice(organizationId, 14m, "SUM-RI");
        BankTransaction openBank = PersistenceTestData.CreateBankTransaction(organizationId, 15m, reference: "SUM-OB");
        BankTransaction dismissedBank = PersistenceTestData.CreateBankTransaction(organizationId, 16m, reference: "SUM-DB");

        ReviewCase openCompeting = OpenCompeting(organizationId, competingOpenInvoice, competingOpenBank, OpenedAt);
        ReviewCase resolvedCompeting = OpenCompeting(organizationId, competingResolvedInvoice, competingResolvedBank, OpenedAt);
        Assert.True(resolvedCompeting.Resolve(ReviewCaseOutcome.HandledOutside, null, null, ClosedAt).IsSuccess);
        ReviewCase dismissedCompeting = OpenCompeting(organizationId, competingDismissedInvoice, competingDismissedBank, OpenedAt);
        Assert.True(dismissedCompeting.Dismiss(null, ClosedAt).IsSuccess);
        ReviewCase openInvoiceCase = OpenInvestigation(organizationId, openInvoice, null, OpenedAt, "open-invoice");
        ReviewCase resolvedInvoiceCase = OpenInvestigation(organizationId, resolvedInvoice, null, OpenedAt, "resolved-invoice");
        Assert.True(resolvedInvoiceCase.Resolve(ReviewCaseOutcome.NoActionRequired, null, null, ClosedAt).IsSuccess);
        ReviewCase openBankCase = OpenInvestigation(organizationId, null, openBank, OpenedAt, "open-bank");
        ReviewCase dismissedBankCase = OpenInvestigation(organizationId, null, dismissedBank, OpenedAt, "dismissed-bank");
        Assert.True(dismissedBankCase.Dismiss("closed", ClosedAt).IsSuccess);

        await using ReconciliationDbContext context = _fixture.CreateContext();
        context.AddRange(
            competingOpenInvoice,
            competingOpenBank,
            competingResolvedInvoice,
            competingResolvedBank,
            competingDismissedInvoice,
            competingDismissedBank,
            openInvoice,
            resolvedInvoice,
            openBank,
            dismissedBank,
            openCompeting,
            resolvedCompeting,
            dismissedCompeting,
            openInvoiceCase,
            resolvedInvoiceCase,
            openBankCase,
            dismissedBankCase);
        await context.SaveChangesAsync();
    }

    private async Task SeedOpenInvestigationsAsync(OrganizationId organizationId, int count)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        for (int index = 0; index < count; index++)
        {
            Invoice invoice = PersistenceTestData.CreateInvoice(
                organizationId,
                10m,
                "SUM-" + organizationId.Value.ToString("N")[..8] + "-" + index.ToString("000"));
            context.Add(invoice);
            context.Add(OpenInvestigation(organizationId, invoice, null, OpenedAt, "bulk"));
        }

        await context.SaveChangesAsync();
    }

    private static ReviewCase OpenCompeting(
        OrganizationId organizationId,
        Invoice invoice,
        BankTransaction bank,
        DateTimeOffset openedAt)
    {
        return ReviewCase.Open(
            ReviewCaseId.New(),
            organizationId,
            ReviewCaseReason.CompetingMatch,
            invoice.Id,
            bank.Id,
            null,
            null,
            openedAt);
    }

    private static ReviewCase OpenInvestigation(
        OrganizationId organizationId,
        Invoice? invoice,
        BankTransaction? bank,
        DateTimeOffset openedAt,
        string note)
    {
        return ReviewCase.Open(
            ReviewCaseId.New(),
            organizationId,
            ReviewCaseReason.NeedsInvestigation,
            invoice?.Id,
            bank?.Id,
            null,
            note,
            openedAt);
    }

    private async Task<byte[]> ReadRowVersionAsync(ReviewCaseId reviewCaseId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        ReviewCase reviewCase = await context.ReviewCases.SingleAsync(item => item.Id == reviewCaseId);
        return context.Entry(reviewCase).Property<byte[]>("RowVersion").CurrentValue.ToArray();
    }

    private async Task<int> AuditCountAsync(OrganizationId organizationId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        return await context.ReviewCaseAuditEntries.CountAsync(item => item.OrganizationId == organizationId);
    }

    private static async Task<ReviewCaseSummaryResponse> ReadSummaryAsync(HttpClient client)
    {
        string body = await ReadBodyAsync(client, "/api/review-cases/summary");
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;
        Assert.Equal(6, root.EnumerateObject().Count());
        ReviewCaseSummaryResponse summary = new(
            root.GetProperty("totalCount").GetInt64(),
            root.GetProperty("openCount").GetInt64(),
            root.GetProperty("resolvedCount").GetInt64(),
            root.GetProperty("dismissedCount").GetInt64(),
            root.GetProperty("openCompetingMatchCount").GetInt64(),
            root.GetProperty("openNeedsInvestigationCount").GetInt64());
        AssertIdentities(summary);
        return summary;
    }

    private static async Task<string> ReadBodyAsync(HttpClient client, string path)
    {
        HttpResponseMessage response = await client.GetAsync(path);
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        return body;
    }

    private static int CountProperties(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject().Count();
    }

    private static void AssertZeros(ReviewCaseSummaryResponse summary)
    {
        Assert.Equal(0, summary.TotalCount);
        Assert.Equal(0, summary.OpenCount);
        Assert.Equal(0, summary.ResolvedCount);
        Assert.Equal(0, summary.DismissedCount);
        Assert.Equal(0, summary.OpenCompetingMatchCount);
        Assert.Equal(0, summary.OpenNeedsInvestigationCount);
    }

    private static void AssertIdentities(ReviewCaseSummaryResponse summary)
    {
        Assert.Equal(summary.TotalCount, summary.OpenCount + summary.ResolvedCount + summary.DismissedCount);
        Assert.Equal(summary.OpenCount, summary.OpenCompetingMatchCount + summary.OpenNeedsInvestigationCount);
        Assert.True(summary.TotalCount >= 0);
        Assert.True(summary.OpenCount >= 0);
        Assert.True(summary.ResolvedCount >= 0);
        Assert.True(summary.DismissedCount >= 0);
        Assert.True(summary.OpenCompetingMatchCount >= 0);
        Assert.True(summary.OpenNeedsInvestigationCount >= 0);
    }

    private static void RequireAggregateCommand(SummarySqlCapture capture, OrganizationId organizationId)
    {
        CapturedSql command = Assert.Single(capture.Commands);
        string withoutBig = command.Text.Replace("COUNT_BIG", string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ReviewCases", command.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("COUNT_BIG", command.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("COUNT(", withoutBig, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ELSE", command.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OFFSET", command.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("JOIN", command.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Invoices", command.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BankTransactions", command.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ReviewCaseAudit", command.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(6, CountOccurrences(command.Text, "COUNT_BIG"));
        Assert.Contains(
            command.Parameters,
            value => value is Guid guid && guid == organizationId.Value);
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static async Task AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode statusCode,
        string code)
    {
        string body = await response.Content.ReadAsStringAsync();
        Assert.Equal(statusCode, response.StatusCode);
        using JsonDocument document = JsonDocument.Parse(body);
        Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
    }

    private sealed class SummarySqlCapture : DbCommandInterceptor
    {
        public List<CapturedSql> Commands { get; } = [];

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Commands.Add(CapturedSql.From(command));
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(CapturedSql.From(command));
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            Commands.Add(CapturedSql.From(command));
            return base.NonQueryExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(CapturedSql.From(command));
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed record CapturedSql(string Text, IReadOnlyList<object?> Parameters)
    {
        public static CapturedSql From(DbCommand command)
        {
            List<object?> values = [];
            foreach (DbParameter parameter in command.Parameters)
            {
                values.Add(parameter.Value is DBNull ? null : parameter.Value);
            }

            return new CapturedSql(command.CommandText, values);
        }
    }
}