using System.Net;
using System.Text;
using System.Text.Json;

using FinOpsDesk.IntegrationTests.Reconciliation.Persistence;
using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.ReviewCases;
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
public sealed class ReviewCaseEndpointTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SqlServerFixture _fixture;
    private readonly FinOpsDeskApiFactory _factory;

    public ReviewCaseEndpointTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
        _factory = new FinOpsDeskApiFactory(fixture.ConnectionString);
    }

    [Theory]
    [InlineData("GET", "/api/review-cases")]
    [InlineData("GET", "/api/review-cases/11111111-1111-1111-1111-111111111111")]
    [InlineData("POST", "/api/review-cases")]
    [InlineData("POST", "/api/review-cases/11111111-1111-1111-1111-111111111111/resolve")]
    [InlineData("POST", "/api/review-cases/11111111-1111-1111-1111-111111111111/dismiss")]
    public async Task Missing_bearer_token_returns_401(string method, string path)
    {
        using HttpClient client = _factory.CreateApiClient();

        HttpResponseMessage response = await SendAsync(client, method, path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/api/review-cases")]
    [InlineData("GET", "/api/review-cases/11111111-1111-1111-1111-111111111111")]
    [InlineData("POST", "/api/review-cases")]
    [InlineData("POST", "/api/review-cases/11111111-1111-1111-1111-111111111111/resolve")]
    [InlineData("POST", "/api/review-cases/11111111-1111-1111-1111-111111111111/dismiss")]
    public async Task Invalid_bearer_token_returns_401(string method, string path)
    {
        using HttpClient client = _factory.CreateApiClient(
            TestAccessTokens.CreateSignedWithOtherKey(Guid.NewGuid()));

        HttpResponseMessage response = await SendAsync(client, method, path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/api/review-cases")]
    [InlineData("GET", "/api/review-cases/11111111-1111-1111-1111-111111111111")]
    [InlineData("POST", "/api/review-cases")]
    [InlineData("POST", "/api/review-cases/11111111-1111-1111-1111-111111111111/resolve")]
    [InlineData("POST", "/api/review-cases/11111111-1111-1111-1111-111111111111/dismiss")]
    public async Task Missing_organization_claim_returns_403(string method, string path)
    {
        using HttpClient client = _factory.CreateApiClient(
            TestAccessTokens.CreateWithoutOrganizationClaim());

        HttpResponseMessage response = await SendAsync(client, method, path);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Unusable_organization_claim_returns_403(string organizationClaim)
    {
        using HttpClient client = _factory.CreateApiClient(
            TestAccessTokens.CreateWithOrganizationClaim(organizationClaim));

        HttpResponseMessage response = await client.GetAsync("/api/review-cases");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Tenant_member_can_list_review_cases()
    {
        OrganizationId organizationId = OrganizationId.New();
        using HttpClient client = Client(organizationId);

        HttpResponseMessage response = await client.GetAsync("/api/review-cases");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = await ReadJsonAsync(response);
        Assert.Equal(0, document.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, document.RootElement.GetProperty("page").GetInt32());
        Assert.Equal(50, document.RootElement.GetProperty("pageSize").GetInt32());
    }

    [Fact]
    public async Task Open_returns_201_then_200_for_the_same_open_subject()
    {
        OrganizationId organizationId = OrganizationId.New();
        (Invoice invoice, BankTransaction bank) = await SeedPairAsync(organizationId, 100m, 80m);
        FinancialSnapshot before = await ReadFinancialAsync(organizationId, invoice.Id, bank.Id);
        using HttpClient client = Client(organizationId);

        HttpResponseMessage created = await PostJsonAsync(
            client,
            "/api/review-cases",
            new
            {
                reason = "CompetingMatch",
                invoiceId = invoice.Id.Value,
                bankTransactionId = bank.Id.Value,
                sourceLabel = " exact-1 ",
                note = "opening note",
            });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using JsonDocument createdDocument = await ReadJsonAsync(created);
        JsonElement createdCase = createdDocument.RootElement;
        Guid reviewCaseId = createdCase.GetProperty("reviewCaseId").GetGuid();
        string token = createdCase.GetProperty("concurrencyToken").GetString()!;
        Assert.Equal($"/api/review-cases/{reviewCaseId:D}", LocationPath(created));
        AssertCaseShape(createdCase, "CompetingMatch", "Open", "exact-1", "opening note");
        Assert.False(createdCase.TryGetProperty("created", out _));
        Assert.Equal(JsonValueKind.String, createdCase.GetProperty("reason").ValueKind);
        Assert.Equal(JsonValueKind.String, createdCase.GetProperty("status").ValueKind);

        HttpResponseMessage replayed = await PostJsonAsync(
            client,
            "/api/review-cases",
            new
            {
                reason = "CompetingMatch",
                invoiceId = invoice.Id.Value,
                bankTransactionId = bank.Id.Value,
                sourceLabel = "smart-1",
                note = "replacement note",
            });

        Assert.Equal(HttpStatusCode.OK, replayed.StatusCode);
        Assert.Null(replayed.Headers.Location);
        using JsonDocument replayedDocument = await ReadJsonAsync(replayed);
        JsonElement replayedCase = replayedDocument.RootElement;
        Assert.Equal(reviewCaseId, replayedCase.GetProperty("reviewCaseId").GetGuid());
        Assert.Equal("opening note", replayedCase.GetProperty("note").GetString());
        Assert.Equal("exact-1", replayedCase.GetProperty("sourceLabel").GetString());
        Assert.Equal(token, replayedCase.GetProperty("concurrencyToken").GetString());
        Assert.Equal(token, await TokenAsync(reviewCaseId));
        AssertSame(before, await ReadFinancialAsync(organizationId, invoice.Id, bank.Id));
    }

    [Fact]
    public async Task Open_distinguishes_subjects_and_allows_a_new_case_after_a_terminal_case()
    {
        OrganizationId organizationId = OrganizationId.New();
        (Invoice invoice, BankTransaction bank) = await SeedPairAsync(organizationId);
        Invoice otherInvoice = PersistenceTestData.CreateInvoice(organizationId, 25m, "INV-OTHER");
        await SeedAsync(otherInvoice);
        using HttpClient client = Client(organizationId);

        Guid competing = await OpenAsync(client, "CompetingMatch", invoice.Id.Value, bank.Id.Value, "manual", "pair");
        Guid invoiceOnly = await OpenAsync(client, "NeedsInvestigation", invoice.Id.Value, null, "manual", "invoice");
        Guid bankOnly = await OpenAsync(client, "NeedsInvestigation", null, bank.Id.Value, null, null);
        Assert.NotEqual(competing, invoiceOnly);
        Assert.NotEqual(competing, bankOnly);
        Assert.NotEqual(invoiceOnly, bankOnly);

        string token = await TokenAsync(invoiceOnly);
        HttpResponseMessage dismissed = await PostJsonAsync(
            client,
            $"/api/review-cases/{invoiceOnly:D}/dismiss",
            new { concurrencyToken = token, resolutionNote = "closed" });
        Assert.Equal(HttpStatusCode.OK, dismissed.StatusCode);

        HttpResponseMessage reopened = await PostJsonAsync(
            client,
            "/api/review-cases",
            new
            {
                reason = "NeedsInvestigation",
                invoiceId = invoice.Id.Value,
                sourceLabel = "smart-1",
                note = "again",
            });
        Assert.Equal(HttpStatusCode.Created, reopened.StatusCode);
        using JsonDocument document = await ReadJsonAsync(reopened);
        Assert.NotEqual(invoiceOnly, document.RootElement.GetProperty("reviewCaseId").GetGuid());
        Assert.Equal("again", document.RootElement.GetProperty("note").GetString());
    }

    [Theory]
    [InlineData("""{"reason":1,"invoiceId":"INVOICE"}""")]
    [InlineData("""{"reason":"1","invoiceId":"INVOICE"}""")]
    [InlineData("""{"reason":"competingmatch","invoiceId":"INVOICE","bankTransactionId":"BANK"}""")]
    [InlineData("""{"reason":"CompetingMatch","invoiceId":"INVOICE"}""")]
    [InlineData("""{"reason":"NeedsInvestigation","invoiceId":"INVOICE","bankTransactionId":"BANK"}""")]
    [InlineData("""{"reason":"NeedsInvestigation"}""")]
    [InlineData("""{"reason":"NeedsInvestigation","invoiceId":"00000000-0000-0000-0000-000000000000"}""")]
    [InlineData("""{"reason":"NeedsInvestigation","invoiceId":"INVOICE","organizationId":"ORG"}""")]
    [InlineData("""{"reason":"NeedsInvestigation","invoiceId":"INVOICE","subjectKey":"x"}""")]
    [InlineData("""{"invoiceId":"INVOICE"}""")]
    [InlineData("""{"reason":"NeedsInvestigation","invoiceId":"INVOICE","sourceLabel":"Manual"}""")]
    [InlineData("""{"reason":"NeedsInvestigation","invoiceId":123}""")]
    [InlineData("""{"reason":"NeedsInvestigation","invoiceId":"INVOICE","note":1}""")]
    [InlineData("{")]
    public async Task Open_rejects_invalid_request_shapes(string payload)
    {
        OrganizationId organizationId = OrganizationId.New();
        (Invoice invoice, BankTransaction bank) = await SeedPairAsync(organizationId);
        FinancialSnapshot before = await ReadFinancialAsync(organizationId, invoice.Id, bank.Id);
        string body = payload
            .Replace("INVOICE", invoice.Id.Value.ToString("D"), StringComparison.Ordinal)
            .Replace("BANK", bank.Id.Value.ToString("D"), StringComparison.Ordinal)
            .Replace("ORG", organizationId.Value.ToString("D"), StringComparison.Ordinal);
        using HttpClient client = Client(organizationId);

        await AssertProblemAsync(
            await PostRawAsync(client, "/api/review-cases", body),
            HttpStatusCode.BadRequest,
            ReconciliationApplicationErrors.ReviewCaseInvalidRequest.Code,
            body);
        Assert.Equal(0, await CaseCountAsync(organizationId));
        AssertSame(before, await ReadFinancialAsync(organizationId, invoice.Id, bank.Id));
    }

    [Fact]
    public async Task Open_rejects_a_note_longer_than_500_characters()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 10m, "INV-LONG");
        await SeedAsync(invoice);
        using HttpClient client = Client(organizationId);
        string body = JsonSerializer.Serialize(
            new
            {
                reason = "NeedsInvestigation",
                invoiceId = invoice.Id.Value,
                note = new string('n', ReviewCase.MaxNoteLength + 1),
            },
            JsonOptions);

        await AssertProblemAsync(
            await PostRawAsync(client, "/api/review-cases", body),
            HttpStatusCode.BadRequest,
            ReconciliationApplicationErrors.ReviewCaseInvalidRequest.Code);
        Assert.Equal(0, await CaseCountAsync(organizationId));
    }

    [Fact]
    public async Task Open_rejects_duplicate_json_properties()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 10m, "INV-DUP");
        await SeedAsync(invoice);
        using HttpClient client = Client(organizationId);
        string body =
            $$"""{"reason":"NeedsInvestigation","reason":"NeedsInvestigation","invoiceId":"{{invoice.Id.Value:D}}"}""";

        await AssertProblemAsync(
            await PostRawAsync(client, "/api/review-cases", body),
            HttpStatusCode.BadRequest,
            ReconciliationApplicationErrors.ReviewCaseInvalidRequest.Code);
    }

    [Fact]
    public async Task Open_rejects_an_unsupported_media_type_and_an_oversized_body()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 10m, "INV-SIZE");
        await SeedAsync(invoice);
        FinancialSnapshot before = await ReadFinancialAsync(organizationId, invoice.Id, null);
        using HttpClient client = Client(organizationId);

        await AssertProblemAsync(
            await PostRawAsync(
                client,
                "/api/review-cases",
                $$"""{"reason":"NeedsInvestigation","invoiceId":"{{invoice.Id.Value:D}}"}""",
                "text/plain"),
            HttpStatusCode.UnsupportedMediaType,
            ReconciliationApplicationErrors.ReviewCaseUnsupportedMediaType.Code);
        await AssertProblemAsync(
            await PostRawAsync(client, "/api/review-cases", new string('x', 4097)),
            HttpStatusCode.RequestEntityTooLarge,
            ReconciliationApplicationErrors.ReviewCaseRequestTooLarge.Code);
        Assert.Equal(0, await CaseCountAsync(organizationId));
        AssertSame(before, await ReadFinancialAsync(organizationId, invoice.Id, null));
    }

    [Fact]
    public async Task Open_returns_404_for_missing_or_foreign_financial_ids()
    {
        OrganizationId organizationId = OrganizationId.New();
        OrganizationId otherOrganizationId = OrganizationId.New();
        (Invoice invoice, BankTransaction bank) = await SeedPairAsync(organizationId);
        Invoice foreignInvoice = PersistenceTestData.CreateInvoice(otherOrganizationId, 1234.25m, "FOREIGN-INV");
        BankTransaction foreignBank = PersistenceTestData.CreateBankTransaction(
            otherOrganizationId,
            88m,
            reference: "FOREIGN-BANK");
        await SeedAsync(foreignInvoice, foreignBank);
        FinancialSnapshot before = await ReadFinancialAsync(organizationId, invoice.Id, bank.Id);
        using HttpClient client = Client(organizationId);

        await AssertProblemAsync(
            await PostJsonAsync(
                client,
                "/api/review-cases",
                new { reason = "NeedsInvestigation", invoiceId = Guid.NewGuid() }),
            HttpStatusCode.NotFound,
            ReconciliationApplicationErrors.InvoiceNotFound.Code);
        string foreignInvoiceBody = await AssertProblemAsync(
            await PostJsonAsync(
                client,
                "/api/review-cases",
                new { reason = "NeedsInvestigation", invoiceId = foreignInvoice.Id.Value }),
            HttpStatusCode.NotFound,
            ReconciliationApplicationErrors.InvoiceNotFound.Code);
        await AssertProblemAsync(
            await PostJsonAsync(
                client,
                "/api/review-cases",
                new { reason = "NeedsInvestigation", bankTransactionId = Guid.NewGuid() }),
            HttpStatusCode.NotFound,
            ReconciliationApplicationErrors.BankTransactionNotFound.Code);
        string foreignBankBody = await AssertProblemAsync(
            await PostJsonAsync(
                client,
                "/api/review-cases",
                new { reason = "NeedsInvestigation", bankTransactionId = foreignBank.Id.Value }),
            HttpStatusCode.NotFound,
            ReconciliationApplicationErrors.BankTransactionNotFound.Code);

        Assert.DoesNotContain("FOREIGN-INV", foreignInvoiceBody, StringComparison.Ordinal);
        Assert.DoesNotContain("1234.25", foreignInvoiceBody, StringComparison.Ordinal);
        Assert.DoesNotContain("FOREIGN-BANK", foreignBankBody, StringComparison.Ordinal);
        Assert.Equal(0, await CaseCountAsync(organizationId));
        AssertSame(before, await ReadFinancialAsync(organizationId, invoice.Id, bank.Id));
    }

    [Fact]
    public async Task Open_rejects_a_client_organization_query()
    {
        OrganizationId organizationId = OrganizationId.New();
        OrganizationId otherOrganizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 10m, "INV-QUERY");
        await SeedAsync(invoice);
        using HttpClient client = Client(organizationId);

        await AssertProblemAsync(
            await PostJsonAsync(
                client,
                $"/api/review-cases?organizationId={otherOrganizationId.Value:D}",
                new { reason = "NeedsInvestigation", invoiceId = invoice.Id.Value }),
            HttpStatusCode.BadRequest,
            ReconciliationApplicationErrors.ReviewCaseInvalidRequest.Code);
        Assert.Equal(0, await CaseCountAsync(organizationId));
        Assert.Equal(0, await CaseCountAsync(otherOrganizationId));
    }

    [Fact]
    public async Task List_uses_bounded_pagination_and_filters()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice first = PersistenceTestData.CreateInvoice(organizationId, 10m, "INV-P1");
        Invoice second = PersistenceTestData.CreateInvoice(organizationId, 20m, "INV-P2");
        Invoice third = PersistenceTestData.CreateInvoice(organizationId, 30m, "INV-P3");
        BankTransaction bank = PersistenceTestData.CreateBankTransaction(organizationId, 40m, reference: "BANK-P");
        await SeedAsync(first, second, third, bank);
        using HttpClient client = Client(organizationId);
        Guid firstCase = await OpenAsync(client, "NeedsInvestigation", first.Id.Value, null, "manual", "one");
        Guid secondCase = await OpenAsync(client, "NeedsInvestigation", second.Id.Value, null, "manual", "two");
        Guid bankCase = await OpenAsync(client, "NeedsInvestigation", null, bank.Id.Value, "manual", "bank");
        await DismissAsync(client, secondCase, "done");

        using JsonDocument defaults = await ReadJsonAsync(await client.GetAsync("/api/review-cases"));
        Assert.Equal(1, defaults.RootElement.GetProperty("page").GetInt32());
        Assert.Equal(50, defaults.RootElement.GetProperty("pageSize").GetInt32());
        Assert.Equal(3, defaults.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(3, defaults.RootElement.GetProperty("items").GetArrayLength());
        Assert.False(defaults.RootElement.GetProperty("items")[0].TryGetProperty("linkedFinancials", out _));

        using JsonDocument wide = await ReadJsonAsync(await client.GetAsync("/api/review-cases?page=1&pageSize=100"));
        Assert.Equal(100, wide.RootElement.GetProperty("pageSize").GetInt32());
        Assert.Equal(3, wide.RootElement.GetProperty("totalCount").GetInt32());

        using JsonDocument page = await ReadJsonAsync(await client.GetAsync("/api/review-cases?page=2&pageSize=2"));
        Assert.Equal(2, page.RootElement.GetProperty("page").GetInt32());
        Assert.Equal(2, page.RootElement.GetProperty("pageSize").GetInt32());
        Assert.Equal(3, page.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Single(page.RootElement.GetProperty("items").EnumerateArray());

        using JsonDocument open = await ReadJsonAsync(await client.GetAsync("/api/review-cases?status=Open"));
        Assert.Equal(2, open.RootElement.GetProperty("totalCount").GetInt32());
        Assert.All(open.RootElement.GetProperty("items").EnumerateArray(), item =>
            Assert.Equal("Open", item.GetProperty("status").GetString()));

        using JsonDocument dismissed = await ReadJsonAsync(await client.GetAsync("/api/review-cases?status=Dismissed"));
        Assert.Equal(1, dismissed.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(secondCase, dismissed.RootElement.GetProperty("items")[0].GetProperty("reviewCaseId").GetGuid());

        using JsonDocument byInvoice = await ReadJsonAsync(
            await client.GetAsync($"/api/review-cases?invoiceId={first.Id.Value:D}"));
        Assert.Equal(1, byInvoice.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(firstCase, byInvoice.RootElement.GetProperty("items")[0].GetProperty("reviewCaseId").GetGuid());

        using JsonDocument byBank = await ReadJsonAsync(
            await client.GetAsync($"/api/review-cases?bankTransactionId={bank.Id.Value:D}"));
        Assert.Equal(1, byBank.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(bankCase, byBank.RootElement.GetProperty("items")[0].GetProperty("reviewCaseId").GetGuid());
        Assert.False(string.IsNullOrWhiteSpace(
            byBank.RootElement.GetProperty("items")[0].GetProperty("concurrencyToken").GetString()));
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=101")]
    [InlineData("page=2147483647&pageSize=100")]
    [InlineData("page=abc")]
    [InlineData("pageSize=1.5")]
    public async Task List_rejects_invalid_pagination(string query)
    {
        OrganizationId organizationId = OrganizationId.New();
        using HttpClient client = Client(organizationId);

        await AssertProblemAsync(
            await client.GetAsync($"/api/review-cases?{query}"),
            HttpStatusCode.BadRequest,
            ReconciliationApplicationErrors.ReviewCaseInvalidPagination.Code);
    }

    [Theory]
    [InlineData("sort=id")]
    [InlineData("status=1")]
    [InlineData("status=open")]
    [InlineData("invoiceId=00000000-0000-0000-0000-000000000000")]
    [InlineData("bankTransactionId=not-a-guid")]
    [InlineData("organizationId=11111111-1111-1111-1111-111111111111")]
    public async Task List_rejects_unsupported_or_malformed_query_parameters(string query)
    {
        OrganizationId organizationId = OrganizationId.New();
        using HttpClient client = Client(organizationId);

        await AssertProblemAsync(
            await client.GetAsync("/api/review-cases?" + query),
            HttpStatusCode.BadRequest,
            ReconciliationApplicationErrors.ReviewCaseInvalidRequest.Code);
    }

    [Fact]
    public async Task List_orders_by_opened_time_then_review_case_id()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice first = PersistenceTestData.CreateInvoice(organizationId, 10m, "INV-O1");
        Invoice second = PersistenceTestData.CreateInvoice(organizationId, 10m, "INV-O2");
        await SeedAsync(first, second);
        using HttpClient client = Client(organizationId);
        Guid firstCase = await OpenAsync(client, "NeedsInvestigation", first.Id.Value, null, "manual", "one");
        Guid secondCase = await OpenAsync(client, "NeedsInvestigation", second.Id.Value, null, "manual", "two");
        DateTimeOffset openedAt = new(2026, 8, 24, 10, 0, 0, TimeSpan.Zero);
        await ExecuteAsync(
            $"UPDATE [reconciliation].[ReviewCases] SET [OpenedAt] = {openedAt} WHERE [Id] = {firstCase} AND [OrganizationId] = {organizationId.Value}");
        await ExecuteAsync(
            $"UPDATE [reconciliation].[ReviewCases] SET [OpenedAt] = {openedAt} WHERE [Id] = {secondCase} AND [OrganizationId] = {organizationId.Value}");

        using JsonDocument document = await ReadJsonAsync(await client.GetAsync("/api/review-cases"));
        Guid[] actual = document.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("reviewCaseId").GetGuid())
            .ToArray();
        Guid[] expected = await PersistedOrderAsync(organizationId);
        Assert.Equal(expected, actual);
        Assert.Equal(2, actual.Length);
        await using ReconciliationDbContext context = _fixture.CreateContext();
        DateTimeOffset[] opened = await context.ReviewCases
            .AsNoTracking()
            .Where(reviewCase => reviewCase.OrganizationId == organizationId)
            .Select(reviewCase => reviewCase.OpenedAt)
            .ToArrayAsync();
        Assert.Equal(opened[0], opened[1]);
    }

    [Fact]
    public async Task Organization_header_cannot_switch_tenant()
    {
        OrganizationId organizationId = OrganizationId.New();
        OrganizationId otherOrganizationId = OrganizationId.New();
        Invoice ownInvoice = PersistenceTestData.CreateInvoice(organizationId, 10m, "INV-OWN");
        Invoice foreignInvoice = PersistenceTestData.CreateInvoice(otherOrganizationId, 99m, "INV-FOREIGN-HEADER");
        await SeedAsync(ownInvoice, foreignInvoice);
        using HttpClient ownClient = Client(organizationId);
        using HttpClient foreignClient = Client(otherOrganizationId);
        await OpenAsync(ownClient, "NeedsInvestigation", ownInvoice.Id.Value, null, "manual", "own");
        await OpenAsync(foreignClient, "NeedsInvestigation", foreignInvoice.Id.Value, null, "manual", "secret-note");
        using HttpRequestMessage request = new(HttpMethod.Get, "/api/review-cases");
        request.Headers.TryAddWithoutValidation("X-Organization-Id", otherOrganizationId.Value.ToString("D"));

        HttpResponseMessage response = await ownClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(body);
        Assert.Equal(1, document.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal("own", document.RootElement.GetProperty("items")[0].GetProperty("note").GetString());
        Assert.DoesNotContain("secret-note", body, StringComparison.Ordinal);
        Assert.DoesNotContain("INV-FOREIGN-HEADER", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Detail_returns_current_partial_and_full_balances()
    {
        OrganizationId organizationId = OrganizationId.New();
        (Invoice invoice, BankTransaction bank) = await SeedPairAsync(organizationId, 100m, 80m);
        using HttpClient client = Client(organizationId);
        Guid reviewCaseId = await OpenAsync(
            client,
            "CompetingMatch",
            invoice.Id.Value,
            bank.Id.Value,
            "manual",
            "watch");
        FinancialSnapshot beforeRead = await ReadFinancialAsync(organizationId, invoice.Id, bank.Id);

        using JsonDocument full = await ReadJsonAsync(await client.GetAsync($"/api/review-cases/{reviewCaseId:D}"));
        JsonElement linked = full.RootElement.GetProperty("linkedFinancials");
        Assert.True(linked.GetProperty("invoiceFound").GetBoolean());
        Assert.True(linked.GetProperty("bankTransactionFound").GetBoolean());
        AssertMoney(linked.GetProperty("invoiceOutstanding"), 100m, "PLN");
        AssertMoney(linked.GetProperty("bankTransactionUnapplied"), 80m, "PLN");
        Assert.Equal("watch", full.RootElement.GetProperty("reviewCase").GetProperty("note").GetString());
        Assert.False(full.RootElement.TryGetProperty("organizationId", out _));

        await AllocateAsync(invoice.Id, bank.Id, 40m, 30m);
        using JsonDocument partial = await ReadJsonAsync(await client.GetAsync($"/api/review-cases/{reviewCaseId:D}"));
        JsonElement partialLinked = partial.RootElement.GetProperty("linkedFinancials");
        AssertMoney(partialLinked.GetProperty("invoiceOutstanding"), 60m, "PLN");
        AssertMoney(partialLinked.GetProperty("bankTransactionUnapplied"), 50m, "PLN");

        await AllocateAsync(invoice.Id, bank.Id, 60m, 50m);
        FinancialSnapshot beforeFinalRead = await ReadFinancialAsync(organizationId, invoice.Id, bank.Id);
        using JsonDocument zero = await ReadJsonAsync(await client.GetAsync($"/api/review-cases/{reviewCaseId:D}"));
        JsonElement zeroLinked = zero.RootElement.GetProperty("linkedFinancials");
        AssertMoney(zeroLinked.GetProperty("invoiceOutstanding"), 0m, "PLN");
        AssertMoney(zeroLinked.GetProperty("bankTransactionUnapplied"), 0m, "PLN");
        AssertSame(beforeFinalRead, await ReadFinancialAsync(organizationId, invoice.Id, bank.Id));
        Assert.NotEqual(beforeRead.InvoiceAllocated, beforeFinalRead.InvoiceAllocated);

        Guid invoiceOnly = await OpenAsync(client, "NeedsInvestigation", invoice.Id.Value, null, "manual", "invoice only");
        using JsonDocument invoiceDocument = await ReadJsonAsync(await client.GetAsync($"/api/review-cases/{invoiceOnly:D}"));
        JsonElement invoiceLinked = invoiceDocument.RootElement.GetProperty("linkedFinancials");
        Assert.True(invoiceLinked.GetProperty("invoiceFound").GetBoolean());
        Assert.False(invoiceLinked.GetProperty("bankTransactionFound").GetBoolean());
        Assert.Equal(JsonValueKind.Null, invoiceLinked.GetProperty("bankTransactionUnapplied").ValueKind);
    }

    [Fact]
    public async Task Detail_returns_500_for_inconsistent_linked_money_without_amounts()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 100m, "CORRUPT-INV");
        await SeedAsync(invoice);
        using HttpClient client = Client(organizationId);
        Guid reviewCaseId = await OpenAsync(client, "NeedsInvestigation", invoice.Id.Value, null, "manual", "corrupt");
        await ExecuteAsync(
            $"UPDATE [reconciliation].[Invoices] SET [AllocatedAmount] = {30m}, [AllocatedCurrency] = {"EUR"} WHERE [Id] = {invoice.Id.Value} AND [OrganizationId] = {organizationId.Value}");
        FinancialSnapshot before = await ReadFinancialAsync(organizationId, invoice.Id, null);

        string body = await AssertProblemAsync(
            await client.GetAsync($"/api/review-cases/{reviewCaseId:D}"),
            HttpStatusCode.InternalServerError,
            ReconciliationApplicationErrors.ReviewCaseInconsistentLinkedMoney.Code);
        using JsonDocument problem = JsonDocument.Parse(body);
        string detail = problem.RootElement.GetProperty("detail").GetString()!;
        Assert.DoesNotContain("EUR", body, StringComparison.Ordinal);
        Assert.DoesNotContain("CORRUPT-INV", body, StringComparison.Ordinal);
        Assert.DoesNotContain("30", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("SqlException", body, StringComparison.Ordinal);
        Assert.DoesNotContain("AllocatedCurrency", body, StringComparison.Ordinal);
        Assert.False(problem.RootElement.TryGetProperty("amount", out _));

        using JsonDocument list = await ReadJsonAsync(await client.GetAsync("/api/review-cases"));
        Assert.Contains(
            list.RootElement.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("reviewCaseId").GetGuid() == reviewCaseId);
        await AssertProblemAsync(
            await client.GetAsync($"/api/review-cases/{Guid.NewGuid():D}"),
            HttpStatusCode.NotFound,
            ReconciliationApplicationErrors.ReviewCaseNotFound.Code);
        AssertSame(before, await ReadFinancialAsync(organizationId, invoice.Id, null));
        Assert.Equal("corrupt", await NoteAsync(reviewCaseId));
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Detail_rejects_a_malformed_identifier(string id)
    {
        OrganizationId organizationId = OrganizationId.New();
        using HttpClient client = Client(organizationId);

        await AssertProblemAsync(
            await client.GetAsync("/api/review-cases/" + id),
            HttpStatusCode.BadRequest,
            ReconciliationApplicationErrors.ReviewCaseInvalidRequest.Code);
    }

    [Fact]
    public async Task Another_tenant_cannot_read_or_close_a_case()
    {
        OrganizationId organizationId = OrganizationId.New();
        OrganizationId otherOrganizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 77m, "INV-SECRET");
        await SeedAsync(invoice);
        using HttpClient owner = Client(organizationId);
        using HttpClient intruder = Client(otherOrganizationId);
        Guid reviewCaseId = await OpenAsync(owner, "NeedsInvestigation", invoice.Id.Value, null, "manual", "secret-note");
        string token = await TokenAsync(reviewCaseId);

        string readBody = await AssertProblemAsync(
            await intruder.GetAsync($"/api/review-cases/{reviewCaseId:D}"),
            HttpStatusCode.NotFound,
            ReconciliationApplicationErrors.ReviewCaseNotFound.Code);
        string resolveBody = await AssertProblemAsync(
            await PostJsonAsync(
                intruder,
                $"/api/review-cases/{reviewCaseId:D}/resolve",
                new { outcome = "NoActionRequired", concurrencyToken = token }),
            HttpStatusCode.NotFound,
            ReconciliationApplicationErrors.ReviewCaseNotFound.Code);
        string dismissBody = await AssertProblemAsync(
            await PostJsonAsync(
                intruder,
                $"/api/review-cases/{reviewCaseId:D}/dismiss",
                new { concurrencyToken = token }),
            HttpStatusCode.NotFound,
            ReconciliationApplicationErrors.ReviewCaseNotFound.Code);
        using JsonDocument list = await ReadJsonAsync(await intruder.GetAsync("/api/review-cases"));

        Assert.Equal(0, list.RootElement.GetProperty("totalCount").GetInt32());
        AssertDoesNotReveal(readBody, "secret-note", "INV-SECRET");
        AssertDoesNotReveal(resolveBody, "secret-note", "INV-SECRET");
        AssertDoesNotReveal(dismissBody, "secret-note", "INV-SECRET");
        Assert.Equal("Open", await StatusAsync(reviewCaseId));
        Assert.Equal(token, await TokenAsync(reviewCaseId));
    }

    [Fact]
    public async Task Resolve_no_action_required_and_handled_outside_do_not_write_financial_rows()
    {
        OrganizationId organizationId = OrganizationId.New();
        (Invoice invoice, BankTransaction bank) = await SeedPairAsync(organizationId, 100m, 90m);
        using HttpClient client = Client(organizationId);
        Guid noAction = await OpenAsync(client, "NeedsInvestigation", invoice.Id.Value, null, "manual", "no action");
        Guid handled = await OpenAsync(client, "NeedsInvestigation", null, bank.Id.Value, "manual", "outside");
        FinancialSnapshot before = await ReadFinancialAsync(organizationId, invoice.Id, bank.Id);

        using JsonDocument noActionDocument = await ReadJsonAsync(await PostJsonAsync(
            client,
            $"/api/review-cases/{noAction:D}/resolve",
            new
            {
                outcome = "NoActionRequired",
                resolutionNote = "nothing to do",
                concurrencyToken = await TokenAsync(noAction),
            }));
        using JsonDocument handledDocument = await ReadJsonAsync(await PostJsonAsync(
            client,
            $"/api/review-cases/{handled:D}/resolve",
            new
            {
                outcome = "HandledOutside",
                concurrencyToken = await TokenAsync(handled),
            }));

        Assert.Equal("Resolved", noActionDocument.RootElement.GetProperty("status").GetString());
        Assert.Equal("NoActionRequired", noActionDocument.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, noActionDocument.RootElement.GetProperty("allocationId").ValueKind);
        Assert.Equal("Resolved", handledDocument.RootElement.GetProperty("status").GetString());
        Assert.Equal("HandledOutside", handledDocument.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.String, noActionDocument.RootElement.GetProperty("outcome").ValueKind);
        AssertSame(before, await ReadFinancialAsync(organizationId, invoice.Id, bank.Id));
    }

    [Fact]
    public async Task Resolve_allocation_recorded_accepts_a_partial_allocation_without_creating_another()
    {
        OrganizationId organizationId = OrganizationId.New();
        (Invoice invoice, BankTransaction bank) = await SeedPairAsync(organizationId, 100m, 100m);
        Allocation allocation = await SeedConfirmedAllocationAsync(invoice, bank, 40m);
        using HttpClient client = Client(organizationId);
        Guid reviewCaseId = await OpenAsync(
            client,
            "CompetingMatch",
            invoice.Id.Value,
            bank.Id.Value,
            "manual",
            "partial");
        string token = await TokenAsync(reviewCaseId);
        FinancialSnapshot before = await ReadFinancialAsync(organizationId, invoice.Id, bank.Id);
        Assert.Equal(40m, before.InvoiceAllocated);
        Assert.Equal(1, before.AllocationCount);

        HttpResponseMessage response = await PostJsonAsync(
            client,
            $"/api/review-cases/{reviewCaseId:D}/resolve",
            new
            {
                outcome = "AllocationRecorded",
                allocationId = allocation.Id.Value,
                resolutionNote = "recorded",
                concurrencyToken = token,
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = await ReadJsonAsync(response);
        Assert.Equal("AllocationRecorded", document.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(allocation.Id.Value, document.RootElement.GetProperty("allocationId").GetGuid());
        Assert.NotEqual(token, document.RootElement.GetProperty("concurrencyToken").GetString());
        AssertSame(before, await ReadFinancialAsync(organizationId, invoice.Id, bank.Id));
    }

    [Fact]
    public async Task Resolve_rejects_missing_foreign_reversed_and_non_covering_allocations()
    {
        OrganizationId organizationId = OrganizationId.New();
        OrganizationId otherOrganizationId = OrganizationId.New();
        (Invoice invoice, BankTransaction bank) = await SeedPairAsync(organizationId, 100m, 100m);
        BankTransaction otherBank = PersistenceTestData.CreateBankTransaction(organizationId, 100m, reference: "OTHER-BANK");
        Invoice foreignInvoice = PersistenceTestData.CreateInvoice(otherOrganizationId, 1234.25m, "FOREIGN-ALLOC-INV");
        BankTransaction foreignBank = PersistenceTestData.CreateBankTransaction(otherOrganizationId, 50m, reference: "FOREIGN-ALLOC");
        await SeedAsync(otherBank, foreignInvoice, foreignBank);
        Allocation nonCovering = await SeedConfirmedAllocationAsync(invoice, otherBank, 10m);
        Allocation reversed = CreateAllocation(organizationId, invoice.Id, bank.Id, 15m);
        Assert.True(reversed.Reverse("reversed in test", new DateTimeOffset(2026, 8, 24, 13, 0, 0, TimeSpan.Zero)).IsSuccess);
        await SeedAsync(reversed);
        Allocation foreign = await SeedConfirmedAllocationAsync(foreignInvoice, foreignBank, 20m);
        using HttpClient client = Client(organizationId);
        Guid reviewCaseId = await OpenAsync(
            client,
            "CompetingMatch",
            invoice.Id.Value,
            bank.Id.Value,
            "manual",
            "cover");
        string token = await TokenAsync(reviewCaseId);
        FinancialSnapshot before = await ReadFinancialAsync(organizationId, invoice.Id, bank.Id);

        await AssertProblemAsync(
            await PostJsonAsync(
                client,
                $"/api/review-cases/{reviewCaseId:D}/resolve",
                new { outcome = "AllocationRecorded", allocationId = Guid.NewGuid(), concurrencyToken = token }),
            HttpStatusCode.Conflict,
            ReconciliationApplicationErrors.ReviewCaseAllocationMismatch.Code);
        string foreignBody = await AssertProblemAsync(
            await PostJsonAsync(
                client,
                $"/api/review-cases/{reviewCaseId:D}/resolve",
                new { outcome = "AllocationRecorded", allocationId = foreign.Id.Value, concurrencyToken = token }),
            HttpStatusCode.Conflict,
            ReconciliationApplicationErrors.ReviewCaseAllocationMismatch.Code);
        await AssertProblemAsync(
            await PostJsonAsync(
                client,
                $"/api/review-cases/{reviewCaseId:D}/resolve",
                new { outcome = "AllocationRecorded", allocationId = reversed.Id.Value, concurrencyToken = token }),
            HttpStatusCode.Conflict,
            ReconciliationApplicationErrors.ReviewCaseAllocationMismatch.Code);
        await AssertProblemAsync(
            await PostJsonAsync(
                client,
                $"/api/review-cases/{reviewCaseId:D}/resolve",
                new { outcome = "AllocationRecorded", allocationId = nonCovering.Id.Value, concurrencyToken = token }),
            HttpStatusCode.Conflict,
            ReconciliationApplicationErrors.ReviewCaseAllocationMismatch.Code);

        Assert.DoesNotContain("FOREIGN-ALLOC-INV", foreignBody, StringComparison.Ordinal);
        Assert.DoesNotContain("1234.25", foreignBody, StringComparison.Ordinal);
        Assert.Equal("Open", await StatusAsync(reviewCaseId));
        Assert.Equal(token, await TokenAsync(reviewCaseId));
        AssertSame(before, await ReadFinancialAsync(organizationId, invoice.Id, bank.Id));
    }

    [Theory]
    [InlineData("""{"outcome":1,"concurrencyToken":"TOKEN"}""", "reconciliation.review_case.invalid_request")]
    [InlineData("""{"outcome":"noactionrequired","concurrencyToken":"TOKEN"}""", "reconciliation.review_case.invalid_request")]
    [InlineData("""{"outcome":"AllocationRecorded","concurrencyToken":"TOKEN"}""", "reconciliation.review_case.allocation_required")]
    [InlineData("""{"outcome":"NoActionRequired","allocationId":"ALLOCATION","concurrencyToken":"TOKEN"}""", "reconciliation.review_case.allocation_forbidden")]
    [InlineData("""{"outcome":"NoActionRequired"}""", "reconciliation.review_case.invalid_concurrency_token")]
    [InlineData("""{"outcome":"NoActionRequired","concurrencyToken":"%%%"}""", "reconciliation.review_case.invalid_concurrency_token")]
    [InlineData("""{"outcome":"NoActionRequired","concurrencyToken":1}""", "reconciliation.review_case.invalid_concurrency_token")]
    public async Task Resolve_rejects_invalid_outcome_shape_and_token(string payload, string code)
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 10m, "INV-SHAPE");
        await SeedAsync(invoice);
        using HttpClient client = Client(organizationId);
        Guid reviewCaseId = await OpenAsync(client, "NeedsInvestigation", invoice.Id.Value, null, "manual", "shape");
        string token = await TokenAsync(reviewCaseId);
        string body = payload
            .Replace("TOKEN", token, StringComparison.Ordinal)
            .Replace("ALLOCATION", Guid.NewGuid().ToString("D"), StringComparison.Ordinal);

        await AssertProblemAsync(
            await PostRawAsync(client, $"/api/review-cases/{reviewCaseId:D}/resolve", body),
            HttpStatusCode.BadRequest,
            code,
            body);
        Assert.Equal("Open", await StatusAsync(reviewCaseId));
        Assert.Equal(token, await TokenAsync(reviewCaseId));
    }

    [Fact]
    public async Task Resolve_replays_an_identical_decision_and_conflicts_on_a_different_note()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 10m, "INV-REPLAY");
        await SeedAsync(invoice);
        using HttpClient client = Client(organizationId);
        Guid reviewCaseId = await OpenAsync(client, "NeedsInvestigation", invoice.Id.Value, null, "manual", "replay");
        string openToken = await TokenAsync(reviewCaseId);
        object firstBody = new
        {
            outcome = "NoActionRequired",
            resolutionNote = "same",
            concurrencyToken = openToken,
        };

        using JsonDocument first = await ReadJsonAsync(await PostJsonAsync(
            client,
            $"/api/review-cases/{reviewCaseId:D}/resolve",
            firstBody));
        string closedToken = first.RootElement.GetProperty("concurrencyToken").GetString()!;
        string closedAt = first.RootElement.GetProperty("closedAt").GetString()!;

        using JsonDocument replay = await ReadJsonAsync(await PostJsonAsync(
            client,
            $"/api/review-cases/{reviewCaseId:D}/resolve",
            new
            {
                outcome = "NoActionRequired",
                resolutionNote = "same",
                concurrencyToken = openToken,
            }));
        Assert.Equal(closedToken, replay.RootElement.GetProperty("concurrencyToken").GetString());
        Assert.Equal(closedAt, replay.RootElement.GetProperty("closedAt").GetString());
        Assert.Equal(closedToken, await TokenAsync(reviewCaseId));

        await AssertProblemAsync(
            await PostJsonAsync(
                client,
                $"/api/review-cases/{reviewCaseId:D}/resolve",
                new
                {
                    outcome = "NoActionRequired",
                    resolutionNote = "different",
                    concurrencyToken = openToken,
                }),
            HttpStatusCode.Conflict,
            ReconciliationErrors.ReviewCaseTerminalConflict.Code);
        Assert.Equal(closedToken, await TokenAsync(reviewCaseId));
        Assert.Equal("same", await ResolutionNoteAsync(reviewCaseId));
    }

    [Fact]
    public async Task Resolve_rejects_a_stale_token_while_the_case_is_open()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 10m, "INV-STALE");
        await SeedAsync(invoice);
        using HttpClient client = Client(organizationId);
        Guid reviewCaseId = await OpenAsync(client, "NeedsInvestigation", invoice.Id.Value, null, "manual", "stale");
        string token = await TokenAsync(reviewCaseId);
        await ExecuteAsync(
            $"UPDATE [reconciliation].[ReviewCases] SET [Note] = {"bumped"} WHERE [Id] = {reviewCaseId} AND [OrganizationId] = {organizationId.Value}");

        await AssertProblemAsync(
            await PostJsonAsync(
                client,
                $"/api/review-cases/{reviewCaseId:D}/resolve",
                new { outcome = "NoActionRequired", concurrencyToken = token }),
            HttpStatusCode.Conflict,
            ReconciliationApplicationErrors.ReviewCaseConcurrencyConflict.Code);
        Assert.Equal("Open", await StatusAsync(reviewCaseId));
        Assert.Equal("bumped", await NoteAsync(reviewCaseId));
        Assert.NotEqual(token, await TokenAsync(reviewCaseId));
    }

    [Fact]
    public async Task Dismiss_succeeds_replays_and_does_not_write_financial_rows()
    {
        OrganizationId organizationId = OrganizationId.New();
        (Invoice invoice, BankTransaction bank) = await SeedPairAsync(organizationId);
        using HttpClient client = Client(organizationId);
        Guid reviewCaseId = await OpenAsync(
            client,
            "CompetingMatch",
            invoice.Id.Value,
            bank.Id.Value,
            "manual",
            "dismiss me");
        string openToken = await TokenAsync(reviewCaseId);
        FinancialSnapshot before = await ReadFinancialAsync(organizationId, invoice.Id, bank.Id);

        using JsonDocument dismissed = await ReadJsonAsync(await PostJsonAsync(
            client,
            $"/api/review-cases/{reviewCaseId:D}/dismiss",
            new { resolutionNote = "not needed", concurrencyToken = openToken }));
        string closedToken = dismissed.RootElement.GetProperty("concurrencyToken").GetString()!;
        string closedAt = dismissed.RootElement.GetProperty("closedAt").GetString()!;
        Assert.Equal("Dismissed", dismissed.RootElement.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, dismissed.RootElement.GetProperty("outcome").ValueKind);
        Assert.Equal(JsonValueKind.Null, dismissed.RootElement.GetProperty("allocationId").ValueKind);

        using JsonDocument replay = await ReadJsonAsync(await PostJsonAsync(
            client,
            $"/api/review-cases/{reviewCaseId:D}/dismiss",
            new { resolutionNote = "not needed", concurrencyToken = openToken }));
        Assert.Equal(closedToken, replay.RootElement.GetProperty("concurrencyToken").GetString());
        Assert.Equal(closedAt, replay.RootElement.GetProperty("closedAt").GetString());
        Assert.Equal(closedToken, await TokenAsync(reviewCaseId));
        AssertSame(before, await ReadFinancialAsync(organizationId, invoice.Id, bank.Id));
    }

    [Fact]
    public async Task Dismiss_conflicts_when_the_note_differs()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 10m, "INV-DISMISS");
        await SeedAsync(invoice);
        using HttpClient client = Client(organizationId);
        Guid reviewCaseId = await OpenAsync(client, "NeedsInvestigation", invoice.Id.Value, null, "manual", "open");
        string openToken = await TokenAsync(reviewCaseId);
        using JsonDocument dismissed = await ReadJsonAsync(await PostJsonAsync(
            client,
            $"/api/review-cases/{reviewCaseId:D}/dismiss",
            new { resolutionNote = "first", concurrencyToken = openToken }));
        string closedToken = dismissed.RootElement.GetProperty("concurrencyToken").GetString()!;

        await AssertProblemAsync(
            await PostJsonAsync(
                client,
                $"/api/review-cases/{reviewCaseId:D}/dismiss",
                new { resolutionNote = "second", concurrencyToken = closedToken }),
            HttpStatusCode.Conflict,
            ReconciliationErrors.ReviewCaseTerminalConflict.Code);
        Assert.Equal("first", await ResolutionNoteAsync(reviewCaseId));
        Assert.Equal(closedToken, await TokenAsync(reviewCaseId));
    }

    [Theory]
    [InlineData("""{"outcome":"NoActionRequired","concurrencyToken":"TOKEN"}""")]
    [InlineData("""{"allocationId":"ALLOCATION","concurrencyToken":"TOKEN"}""")]
    [InlineData("""{"resolutionNote":"x"}""")]
    [InlineData("""{"concurrencyToken":""}""")]
    public async Task Dismiss_rejects_outcome_allocation_and_a_missing_token(string payload)
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 10m, "INV-DBAD");
        await SeedAsync(invoice);
        using HttpClient client = Client(organizationId);
        Guid reviewCaseId = await OpenAsync(client, "NeedsInvestigation", invoice.Id.Value, null, "manual", "stay");
        string token = await TokenAsync(reviewCaseId);
        string body = payload
            .Replace("TOKEN", token, StringComparison.Ordinal)
            .Replace("ALLOCATION", Guid.NewGuid().ToString("D"), StringComparison.Ordinal);
        string code = body.Contains("concurrencyToken\":\"\"", StringComparison.Ordinal) || !body.Contains("concurrencyToken", StringComparison.Ordinal)
            ? ReconciliationApplicationErrors.ReviewCaseInvalidConcurrencyToken.Code
            : ReconciliationApplicationErrors.ReviewCaseInvalidRequest.Code;

        await AssertProblemAsync(
            await PostRawAsync(client, $"/api/review-cases/{reviewCaseId:D}/dismiss", body),
            HttpStatusCode.BadRequest,
            code,
            body);
        Assert.Equal("Open", await StatusAsync(reviewCaseId));
        Assert.Equal(token, await TokenAsync(reviewCaseId));
    }

    [Fact]
    public async Task Resolve_then_dismiss_conflicts_and_dismiss_then_resolve_conflicts()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice resolvedInvoice = PersistenceTestData.CreateInvoice(organizationId, 10m, "INV-RD");
        Invoice dismissedInvoice = PersistenceTestData.CreateInvoice(organizationId, 10m, "INV-DR");
        await SeedAsync(resolvedInvoice, dismissedInvoice);
        using HttpClient client = Client(organizationId);
        Guid resolved = await OpenAsync(client, "NeedsInvestigation", resolvedInvoice.Id.Value, null, "manual", "will resolve");
        Guid dismissed = await OpenAsync(client, "NeedsInvestigation", dismissedInvoice.Id.Value, null, "manual", "will dismiss");
        string resolvedToken = await TokenAsync(resolved);
        string dismissedToken = await TokenAsync(dismissed);
        using JsonDocument resolvedDocument = await ReadJsonAsync(await PostJsonAsync(
            client,
            $"/api/review-cases/{resolved:D}/resolve",
            new { outcome = "HandledOutside", concurrencyToken = resolvedToken }));
        using JsonDocument dismissedDocument = await ReadJsonAsync(await PostJsonAsync(
            client,
            $"/api/review-cases/{dismissed:D}/dismiss",
            new { resolutionNote = "gone", concurrencyToken = dismissedToken }));

        await AssertProblemAsync(
            await PostJsonAsync(
                client,
                $"/api/review-cases/{resolved:D}/dismiss",
                new
                {
                    concurrencyToken = resolvedDocument.RootElement.GetProperty("concurrencyToken").GetString(),
                }),
            HttpStatusCode.Conflict,
            ReconciliationErrors.ReviewCaseTerminalConflict.Code);
        await AssertProblemAsync(
            await PostJsonAsync(
                client,
                $"/api/review-cases/{dismissed:D}/resolve",
                new
                {
                    outcome = "NoActionRequired",
                    concurrencyToken = dismissedDocument.RootElement.GetProperty("concurrencyToken").GetString(),
                }),
            HttpStatusCode.Conflict,
            ReconciliationErrors.ReviewCaseTerminalConflict.Code);
        Assert.Equal("Resolved", await StatusAsync(resolved));
        Assert.Equal("Dismissed", await StatusAsync(dismissed));
        Assert.Equal(
            resolvedDocument.RootElement.GetProperty("concurrencyToken").GetString(),
            await TokenAsync(resolved));
        Assert.Equal(
            dismissedDocument.RootElement.GetProperty("concurrencyToken").GetString(),
            await TokenAsync(dismissed));
    }

    [Fact]
    public async Task OpenApi_document_describes_the_review_case_operations()
    {
        using HttpClient client = _factory.CreateApiClient(TestAccessTokens.Create(Guid.NewGuid()));

        HttpResponseMessage response = await client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = await ReadJsonAsync(response);
        JsonElement paths = document.RootElement.GetProperty("paths");
        JsonElement open = paths.GetProperty("/api/review-cases").GetProperty("post");
        JsonElement list = paths.GetProperty("/api/review-cases").GetProperty("get");
        JsonElement get = paths.GetProperty("/api/review-cases/{id}").GetProperty("get");
        JsonElement resolve = paths.GetProperty("/api/review-cases/{id}/resolve").GetProperty("post");
        JsonElement dismiss = paths.GetProperty("/api/review-cases/{id}/dismiss").GetProperty("post");
        Assert.Equal("OpenReviewCase", open.GetProperty("operationId").GetString());
        Assert.Equal("ListReviewCases", list.GetProperty("operationId").GetString());
        Assert.Equal("GetReviewCase", get.GetProperty("operationId").GetString());
        Assert.Equal("ResolveReviewCase", resolve.GetProperty("operationId").GetString());
        Assert.Equal("DismissReviewCase", dismiss.GetProperty("operationId").GetString());
        Assert.Contains("TenantMember", open.GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Contains("TenantMember", list.GetProperty("description").GetString(), StringComparison.Ordinal);

        JsonElement openSchema = open.GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("schema");
        Assert.Equal("object", SchemaType(openSchema));
        Assert.False(openSchema.GetProperty("additionalProperties").GetBoolean());
        Assert.Contains("reason", RequiredProperties(openSchema));
        AssertEnum(openSchema.GetProperty("properties").GetProperty("reason"), "CompetingMatch", "NeedsInvestigation");
        AssertEnum(openSchema.GetProperty("properties").GetProperty("sourceLabel"), "manual", "exact-1", "smart-1");
        Assert.Equal("string", SchemaType(openSchema.GetProperty("properties").GetProperty("reason")));

        JsonElement resolveSchema = resolve.GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("schema");
        AssertEnum(resolveSchema.GetProperty("properties").GetProperty("outcome"), "AllocationRecorded", "NoActionRequired", "HandledOutside");
        Assert.Contains(
            "Base64",
            resolveSchema.GetProperty("properties").GetProperty("concurrencyToken").GetProperty("description").GetString(),
            StringComparison.Ordinal);
        Assert.Contains("concurrencyToken", RequiredProperties(resolveSchema));
        JsonElement dismissSchema = dismiss.GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("schema");
        Assert.False(dismissSchema.GetProperty("properties").TryGetProperty("outcome", out _));
        Assert.False(dismissSchema.GetProperty("properties").TryGetProperty("allocationId", out _));

        JsonElement page = Parameter(list, "page");
        JsonElement pageSize = Parameter(list, "pageSize");
        Assert.Equal(1, page.GetProperty("schema").GetProperty("default").GetInt32());
        Assert.Equal(50, pageSize.GetProperty("schema").GetProperty("default").GetInt32());
        Assert.Equal(100, pageSize.GetProperty("schema").GetProperty("maximum").GetInt32());
        AssertEnum(Parameter(list, "status").GetProperty("schema"), "Open", "Resolved", "Dismissed");

        Assert.True(open.GetProperty("responses").TryGetProperty("201", out JsonElement created));
        Assert.True(created.GetProperty("headers").TryGetProperty("Location", out _));
        Assert.True(open.GetProperty("responses").TryGetProperty("200", out _));
        Assert.True(open.GetProperty("responses").TryGetProperty("400", out _));
        Assert.True(open.GetProperty("responses").TryGetProperty("404", out _));
        Assert.True(open.GetProperty("responses").TryGetProperty("409", out _));
        Assert.True(get.GetProperty("responses").TryGetProperty("500", out _));
        Assert.True(resolve.GetProperty("responses").TryGetProperty("409", out _));
        Assert.True(list.GetProperty("responses").TryGetProperty("401", out _));
        Assert.True(list.GetProperty("responses").TryGetProperty("403", out _));

        JsonElement caseSchema = document.RootElement
            .GetProperty("components")
            .GetProperty("schemas")
            .GetProperty("ReviewCaseResponse");
        AssertEnum(caseSchema.GetProperty("properties").GetProperty("reason"), "CompetingMatch", "NeedsInvestigation");
        AssertEnum(caseSchema.GetProperty("properties").GetProperty("status"), "Open", "Resolved", "Dismissed");
        Assert.Contains(
            "Base64",
            caseSchema.GetProperty("properties").GetProperty("concurrencyToken").GetProperty("description").GetString(),
            StringComparison.Ordinal);

        bool invoiceHasSecurity = paths.GetProperty("/api/invoices").GetProperty("get").TryGetProperty("security", out _);
        Assert.Equal(invoiceHasSecurity, open.TryGetProperty("security", out _));
        Assert.Equal(invoiceHasSecurity, list.TryGetProperty("security", out _));
        Assert.Equal(invoiceHasSecurity, get.TryGetProperty("security", out _));
        Assert.Equal(invoiceHasSecurity, resolve.TryGetProperty("security", out _));
        Assert.Equal(invoiceHasSecurity, dismiss.TryGetProperty("security", out _));
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    private HttpClient Client(OrganizationId organizationId)
    {
        return _factory.CreateApiClient(TestAccessTokens.Create(organizationId.Value));
    }

    private async Task<Guid> OpenAsync(
        HttpClient client,
        string reason,
        Guid? invoiceId,
        Guid? bankTransactionId,
        string? sourceLabel,
        string? note)
    {
        HttpResponseMessage response = await PostJsonAsync(
            client,
            "/api/review-cases",
            new
            {
                reason,
                invoiceId,
                bankTransactionId,
                sourceLabel,
                note,
            });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using JsonDocument document = await ReadJsonAsync(response);
        return document.RootElement.GetProperty("reviewCaseId").GetGuid();
    }

    private async Task DismissAsync(HttpClient client, Guid reviewCaseId, string note)
    {
        HttpResponseMessage response = await PostJsonAsync(
            client,
            $"/api/review-cases/{reviewCaseId:D}/dismiss",
            new { resolutionNote = note, concurrencyToken = await TokenAsync(reviewCaseId) });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<(Invoice Invoice, BankTransaction BankTransaction)> SeedPairAsync(
        OrganizationId organizationId,
        decimal invoiceAmount = 100m,
        decimal bankAmount = 100m)
    {
        string number = "INV-" + Guid.NewGuid().ToString("N")[..12];
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, invoiceAmount, number);
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(
            organizationId,
            bankAmount,
            reference: number);
        await SeedAsync(invoice, bankTransaction);
        return (invoice, bankTransaction);
    }

    private async Task<Allocation> SeedConfirmedAllocationAsync(
        Invoice invoice,
        BankTransaction bankTransaction,
        decimal amount)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Invoice storedInvoice = await context.Invoices.SingleAsync(candidate => candidate.Id == invoice.Id);
        BankTransaction storedBank = await context.BankTransactions.SingleAsync(
            candidate => candidate.Id == bankTransaction.Id);
        Assert.True(storedInvoice.Allocate(new Money(amount, PersistenceTestData.Pln)).IsSuccess);
        Assert.True(storedBank.Allocate(new Money(amount, PersistenceTestData.Pln)).IsSuccess);
        Allocation allocation = CreateAllocation(
            storedInvoice.OrganizationId,
            storedInvoice.Id,
            storedBank.Id,
            amount);
        context.Add(allocation);
        await context.SaveChangesAsync();
        return allocation;
    }

    private static Allocation CreateAllocation(
        OrganizationId organizationId,
        InvoiceId invoiceId,
        BankTransactionId bankTransactionId,
        decimal amount)
    {
        return new Allocation(
            AllocationId.New(),
            organizationId,
            invoiceId,
            bankTransactionId,
            new Money(amount, PersistenceTestData.Pln),
            new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero),
            Guid.NewGuid(),
            AllocationIdempotencyOperation.ExplicitAmount);
    }

    private async Task AllocateAsync(
        InvoiceId invoiceId,
        BankTransactionId bankTransactionId,
        decimal invoiceAmount,
        decimal bankAmount)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Invoice invoice = await context.Invoices.SingleAsync(candidate => candidate.Id == invoiceId);
        BankTransaction bankTransaction = await context.BankTransactions.SingleAsync(
            candidate => candidate.Id == bankTransactionId);
        Assert.True(invoice.Allocate(new Money(invoiceAmount, PersistenceTestData.Pln)).IsSuccess);
        Assert.True(bankTransaction.Allocate(new Money(bankAmount, PersistenceTestData.Pln)).IsSuccess);
        await context.SaveChangesAsync();
    }

    private async Task SeedAsync(params object[] entities)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        foreach (object entity in entities)
        {
            context.Add(entity);
        }

        await context.SaveChangesAsync();
    }

    private async Task ExecuteAsync(FormattableString sql)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        await context.Database.ExecuteSqlInterpolatedAsync(sql);
    }

    private async Task<FinancialSnapshot> ReadFinancialAsync(
        OrganizationId organizationId,
        InvoiceId? invoiceId,
        BankTransactionId? bankTransactionId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        decimal invoiceAllocated = 0m;
        byte[] invoiceVersion = [];
        if (invoiceId is not null)
        {
            Invoice invoice = await context.Invoices.SingleAsync(candidate => candidate.Id == invoiceId);
            invoiceAllocated = invoice.AllocatedAmount.Amount;
            invoiceVersion = context.Entry(invoice).Property<byte[]>("RowVersion").CurrentValue!.ToArray();
        }

        decimal bankAllocated = 0m;
        byte[] bankVersion = [];
        if (bankTransactionId is not null)
        {
            BankTransaction bankTransaction = await context.BankTransactions.SingleAsync(
                candidate => candidate.Id == bankTransactionId);
            bankAllocated = bankTransaction.AllocatedAmount.Amount;
            bankVersion = context.Entry(bankTransaction).Property<byte[]>("RowVersion").CurrentValue!.ToArray();
        }

        return new FinancialSnapshot(
            invoiceAllocated,
            bankAllocated,
            invoiceVersion,
            bankVersion,
            await context.Allocations.CountAsync(allocation => allocation.OrganizationId == organizationId),
            await context.ImportBatches.CountAsync());
    }

    private async Task<int> CaseCountAsync(OrganizationId organizationId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        return await context.ReviewCases.CountAsync(reviewCase => reviewCase.OrganizationId == organizationId);
    }

    private async Task<string> TokenAsync(Guid reviewCaseId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        ReviewCase reviewCase = await context.ReviewCases.SingleAsync(
            candidate => candidate.Id == new ReviewCaseId(reviewCaseId));
        return ReviewCaseConcurrencyToken.Encode(
            context.Entry(reviewCase).Property<byte[]>("RowVersion").CurrentValue!);
    }

    private async Task<string> StatusAsync(Guid reviewCaseId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        ReviewCase reviewCase = await context.ReviewCases.SingleAsync(
            candidate => candidate.Id == new ReviewCaseId(reviewCaseId));
        return reviewCase.Status.ToString();
    }

    private async Task<string?> NoteAsync(Guid reviewCaseId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        ReviewCase reviewCase = await context.ReviewCases.SingleAsync(
            candidate => candidate.Id == new ReviewCaseId(reviewCaseId));
        return reviewCase.Note;
    }

    private async Task<string?> ResolutionNoteAsync(Guid reviewCaseId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        ReviewCase reviewCase = await context.ReviewCases.SingleAsync(
            candidate => candidate.Id == new ReviewCaseId(reviewCaseId));
        return reviewCase.ResolutionNote;
    }

    private async Task<Guid[]> PersistedOrderAsync(OrganizationId organizationId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        return await context.ReviewCases
            .AsNoTracking()
            .Where(reviewCase => reviewCase.OrganizationId == organizationId)
            .OrderByDescending(reviewCase => reviewCase.OpenedAt)
            .ThenByDescending(reviewCase => reviewCase.Id)
            .Select(reviewCase => reviewCase.Id.Value)
            .ToArrayAsync();
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string method, string path)
    {
        if (method == "GET")
        {
            return await client.GetAsync(path);
        }

        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        return await client.PostAsync(path, content);
    }

    private static async Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string uri, object body)
    {
        using var content = new StringContent(
            JsonSerializer.Serialize(body, JsonOptions),
            Encoding.UTF8,
            "application/json");
        return await client.PostAsync(uri, content);
    }

    private static async Task<HttpResponseMessage> PostRawAsync(
        HttpClient client,
        string uri,
        string body,
        string mediaType = "application/json")
    {
        using var content = new StringContent(body, Encoding.UTF8, mediaType);
        return await client.PostAsync(uri, content);
    }

    private static async Task<string> AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode statusCode,
        string code,
        string? context = null)
    {
        string body = await response.Content.ReadAsStringAsync();
        string because = context is null
            ? $"Body: {body}"
            : $"Payload: {context}{Environment.NewLine}Body: {body}";
        Assert.True(
            response.StatusCode == statusCode,
            $"Expected {(int)statusCode} but got {(int)response.StatusCode}. {because}");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using JsonDocument document = JsonDocument.Parse(body);
        Assert.Equal((int)statusCode, document.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("detail").GetString()));
        return body;
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        string json = await response.Content.ReadAsStringAsync();
        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Created))
        {
            Assert.Fail($"Expected 200 or 201 but got {(int)response.StatusCode}. Body: {json}");
        }

        return JsonDocument.Parse(json);
    }

    private static void AssertCaseShape(
        JsonElement reviewCase,
        string reason,
        string status,
        string sourceLabel,
        string note)
    {
        Assert.Equal(reason, reviewCase.GetProperty("reason").GetString());
        Assert.Equal(status, reviewCase.GetProperty("status").GetString());
        Assert.Equal(sourceLabel, reviewCase.GetProperty("sourceLabel").GetString());
        Assert.Equal(note, reviewCase.GetProperty("note").GetString());
        Assert.False(reviewCase.TryGetProperty("organizationId", out _));
        Assert.False(reviewCase.TryGetProperty("subjectKey", out _));
        Assert.False(reviewCase.TryGetProperty("rowVersion", out _));
        Assert.False(string.IsNullOrWhiteSpace(reviewCase.GetProperty("concurrencyToken").GetString()));
    }

    private static void AssertMoney(JsonElement money, decimal amount, string currency)
    {
        Assert.Equal(JsonValueKind.Number, money.GetProperty("amount").ValueKind);
        Assert.Equal(amount, money.GetProperty("amount").GetDecimal());
        Assert.Equal(currency, money.GetProperty("currency").GetString());
    }

    private static void AssertSame(FinancialSnapshot before, FinancialSnapshot after)
    {
        Assert.Equal(before.InvoiceAllocated, after.InvoiceAllocated);
        Assert.Equal(before.BankAllocated, after.BankAllocated);
        Assert.Equal(before.InvoiceVersion, after.InvoiceVersion);
        Assert.Equal(before.BankVersion, after.BankVersion);
        Assert.Equal(before.AllocationCount, after.AllocationCount);
        Assert.Equal(before.ImportCount, after.ImportCount);
    }

    private static void AssertDoesNotReveal(string body, params string[] values)
    {
        using JsonDocument document = JsonDocument.Parse(body);
        string detail = document.RootElement.GetProperty("detail").GetString() ?? string.Empty;
        string title = document.RootElement.GetProperty("title").GetString() ?? string.Empty;
        foreach (string value in values)
        {
            Assert.DoesNotContain(value, detail, StringComparison.Ordinal);
            Assert.DoesNotContain(value, title, StringComparison.Ordinal);
        }
    }

    private static string LocationPath(HttpResponseMessage response)
    {
        Assert.NotNull(response.Headers.Location);
        return response.Headers.Location.IsAbsoluteUri
            ? response.Headers.Location.AbsolutePath
            : response.Headers.Location.OriginalString;
    }

    private static JsonElement Parameter(JsonElement operation, string name)
    {
        foreach (JsonElement parameter in operation.GetProperty("parameters").EnumerateArray())
        {
            if (parameter.GetProperty("name").GetString() == name)
            {
                return parameter;
            }
        }

        throw new InvalidOperationException($"OpenAPI parameter '{name}' was not found.");
    }

    private static void AssertEnum(JsonElement schema, params string[] values)
    {
        string[] actual = schema.GetProperty("enum").EnumerateArray()
            .Select(item => item.GetString() ?? string.Empty)
            .ToArray();
        Assert.Equal(values, actual);
    }

    private static string SchemaType(JsonElement schema)
    {
        JsonElement type = schema.GetProperty("type");
        if (type.ValueKind == JsonValueKind.Array)
        {
            return type[0].GetString() ?? string.Empty;
        }

        return type.GetString() ?? string.Empty;
    }

    private static HashSet<string> RequiredProperties(JsonElement schema)
    {
        return schema.GetProperty("required")
            .EnumerateArray()
            .Select(item => item.GetString() ?? string.Empty)
            .ToHashSet(StringComparer.Ordinal);
    }

    private sealed record FinancialSnapshot(
        decimal InvoiceAllocated,
        decimal BankAllocated,
        byte[] InvoiceVersion,
        byte[] BankVersion,
        int AllocationCount,
        int ImportCount);
}