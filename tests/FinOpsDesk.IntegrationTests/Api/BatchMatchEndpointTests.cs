using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using FinOpsDesk.IntegrationTests.Reconciliation.Persistence;
using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.BatchExecutions;
using FinOpsDesk.Modules.Reconciliation.Application.Matching;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.BatchExecutions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.Matching;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Repositories;
using FinOpsDesk.Modules.Reconciliation.Presentation.Matching;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FinOpsDesk.IntegrationTests.Api;

[Collection(SqlServerCollection.Name)]
public sealed class BatchMatchEndpointTests : IDisposable
{
    private const string PreviewPath = "/api/matches/batch/preview";

    private const string ExecutePath = "/api/matches/batch/execute";

    private readonly SqlServerFixture _fixture;
    private readonly FinOpsDeskApiFactory _factory;

    public BatchMatchEndpointTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
        _factory = new FinOpsDeskApiFactory(fixture.ConnectionString);
    }

    [Fact]
    public async Task Preview_without_a_bearer_token_returns_401()
    {
        using HttpClient client = _factory.CreateApiClient();

        HttpResponseMessage response = await client.GetAsync(PreviewPath);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Execute_without_a_bearer_token_returns_401()
    {
        using HttpClient client = _factory.CreateApiClient();

        HttpResponseMessage response = await client.SendAsync(ExecuteRequest("{}", Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Preview_without_an_organization_claim_returns_403()
    {
        using HttpClient client = _factory.CreateApiClient(
            TestAccessTokens.CreateWithoutOrganizationClaim());

        HttpResponseMessage response = await client.GetAsync(PreviewPath);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Execute_without_an_organization_claim_returns_403()
    {
        using HttpClient client = _factory.CreateApiClient(
            TestAccessTokens.CreateWithoutOrganizationClaim());

        HttpResponseMessage response = await client.SendAsync(
            ExecuteRequest(PairJson(Guid.NewGuid(), Guid.NewGuid()), Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Cross_tenant_identifiers_do_not_allocate_or_reveal_the_other_organization()
    {
        OrganizationId owner = OrganizationId.New();
        (Invoice invoice, BankTransaction bank) = await SeedPairAsync(owner, "OWN-1", 40m);
        OrganizationId intruder = OrganizationId.New();
        using HttpClient client = _factory.CreateApiClient(TestAccessTokens.Create(intruder.Value));

        HttpResponseMessage response = await client.SendAsync(
            ExecuteRequest(PairJson(invoice.Id.Value, bank.Id.Value), Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = await ReadJsonAsync(response);
        JsonElement item = Assert.Single(document.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("notFound", item.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("allocationId").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("amount").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("currency").ValueKind);
        Assert.False(document.RootElement.GetProperty("allConfirmed").GetBoolean());
        await AssertOutstandingAsync(invoice.Id, 40m);
        Assert.Equal(0, await AllocationCountAsync(owner));
        Assert.Equal(0, await AllocationCountAsync(intruder));
    }

    [Fact]
    public async Task Cross_tenant_idempotency_key_does_not_return_the_other_organizations_result()
    {
        OrganizationId first = OrganizationId.New();
        (Invoice firstInvoice, BankTransaction firstBank) = await SeedPairAsync(first, "KEY-A", 11m);
        OrganizationId second = OrganizationId.New();
        (Invoice secondInvoice, BankTransaction secondBank) = await SeedPairAsync(second, "KEY-B", 12m);
        Guid key = Guid.NewGuid();
        using HttpClient firstClient = _factory.CreateApiClient(TestAccessTokens.Create(first.Value));
        using HttpClient secondClient = _factory.CreateApiClient(TestAccessTokens.Create(second.Value));

        HttpResponseMessage firstResponse = await firstClient.SendAsync(
            ExecuteRequest(PairJson(firstInvoice.Id.Value, firstBank.Id.Value), key));
        HttpResponseMessage secondResponse = await secondClient.SendAsync(
            ExecuteRequest(PairJson(secondInvoice.Id.Value, secondBank.Id.Value), key));

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        using JsonDocument firstDocument = await ReadJsonAsync(firstResponse);
        using JsonDocument secondDocument = await ReadJsonAsync(secondResponse);
        Guid firstAllocation = Item(firstDocument, firstInvoice.Id.Value).GetProperty("allocationId").GetGuid();
        Guid secondAllocation = Item(secondDocument, secondInvoice.Id.Value).GetProperty("allocationId").GetGuid();
        Assert.NotEqual(firstAllocation, secondAllocation);
        Assert.Equal(firstInvoice.Id.Value, Item(firstDocument, firstInvoice.Id.Value).GetProperty("invoiceId").GetGuid());
        Assert.DoesNotContain(
            secondDocument.RootElement.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("invoiceId").GetGuid() == firstInvoice.Id.Value);
        Assert.Equal(1, await AllocationCountAsync(first));
        Assert.Equal(1, await AllocationCountAsync(second));
    }

    [Fact]
    public async Task Forged_organization_header_does_not_change_the_authenticated_tenant()
    {
        OrganizationId authenticated = OrganizationId.New();
        (Invoice invoice, BankTransaction bank) = await SeedPairAsync(authenticated, "HDR", 9m);
        OrganizationId forged = OrganizationId.New();
        using HttpClient client = _factory.CreateApiClient(TestAccessTokens.Create(authenticated.Value));
        HttpRequestMessage request = ExecuteRequest(PairJson(invoice.Id.Value, bank.Id.Value), Guid.NewGuid());
        request.Headers.TryAddWithoutValidation("X-Organization-Id", forged.Value.ToString("D"));

        HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Allocation stored = await context.Allocations.SingleAsync(
            allocation => allocation.InvoiceId == invoice.Id);
        Assert.Equal(authenticated, stored.OrganizationId);
        Assert.Equal(0, await AllocationCountAsync(forged));
    }

    [Fact]
    public async Task Empty_preview_returns_a_complete_empty_result()
    {
        using HttpClient client = ClientFor(OrganizationId.New());

        HttpResponseMessage response = await client.GetAsync(PreviewPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = await ReadJsonAsync(response);
        JsonElement root = document.RootElement;
        Assert.Equal(ExactMatchRules.AlgorithmVersion, root.GetProperty("algorithmVersion").GetString());
        Assert.True(root.GetProperty("inputComplete").GetBoolean());
        Assert.Equal(0, root.GetProperty("eligibleCount").GetInt32());
        Assert.Equal(0, root.GetProperty("excludedByReviewCount").GetInt32());
        Assert.Equal(0, root.GetProperty("ambiguousGroupCount").GetInt32());
        Assert.Equal(0, root.GetProperty("unmatchedCount").GetInt32());
        Assert.Equal(0, root.GetProperty("ineligibleCount").GetInt32());
        Assert.Equal(1, root.GetProperty("page").GetInt32());
        Assert.Equal(50, root.GetProperty("pageSize").GetInt32());
        Assert.Empty(root.GetProperty("eligiblePairs").EnumerateArray());
        Assert.Empty(root.GetProperty("ambiguousGroups").EnumerateArray());
        Assert.Empty(root.GetProperty("currencyTotals").EnumerateArray());
    }

    [Fact]
    public async Task Preview_returns_unique_exact_pairs_and_pages_after_classification()
    {
        OrganizationId organizationId = OrganizationId.New();
        (Invoice later, BankTransaction laterBank) = await SeedPairAsync(organizationId, "BBB", 20m);
        (Invoice earlier, BankTransaction earlierBank) = await SeedPairAsync(organizationId, "AAA", 10m);
        using HttpClient client = ClientFor(organizationId);

        HttpResponseMessage firstPage = await client.GetAsync(PreviewPath + "?pageSize=1");
        HttpResponseMessage secondPage = await client.GetAsync(PreviewPath + "?page=2&pageSize=1");
        HttpResponseMessage defaults = await client.GetAsync(PreviewPath);

        Assert.Equal(HttpStatusCode.OK, firstPage.StatusCode);
        using JsonDocument firstDocument = await ReadJsonAsync(firstPage);
        JsonElement first = firstDocument.RootElement;
        Assert.Equal(2, first.GetProperty("eligibleCount").GetInt32());
        Assert.Equal(1, first.GetProperty("page").GetInt32());
        Assert.Equal(1, first.GetProperty("pageSize").GetInt32());
        JsonElement firstPair = Assert.Single(first.GetProperty("eligiblePairs").EnumerateArray());
        AssertPair(firstPair, earlier, earlierBank, 10m);
        JsonElement total = Assert.Single(first.GetProperty("currencyTotals").EnumerateArray());
        Assert.Equal("PLN", total.GetProperty("currency").GetString());
        Assert.Equal(30m, total.GetProperty("amount").GetDecimal());
        Assert.Equal(2, total.GetProperty("pairCount").GetInt32());

        using JsonDocument secondDocument = await ReadJsonAsync(secondPage);
        JsonElement secondPair = Assert.Single(
            secondDocument.RootElement.GetProperty("eligiblePairs").EnumerateArray());
        AssertPair(secondPair, later, laterBank, 20m);
        Assert.Equal(30m, Assert.Single(
            secondDocument.RootElement.GetProperty("currencyTotals").EnumerateArray()).GetProperty("amount").GetDecimal());

        using JsonDocument defaultDocument = await ReadJsonAsync(defaults);
        Assert.Equal(1, defaultDocument.RootElement.GetProperty("page").GetInt32());
        Assert.Equal(50, defaultDocument.RootElement.GetProperty("pageSize").GetInt32());
        Assert.Equal(2, defaultDocument.RootElement.GetProperty("eligiblePairs").GetArrayLength());
        Assert.Equal(
            earlier.Id.Value,
            defaultDocument.RootElement.GetProperty("eligiblePairs")[0].GetProperty("invoiceId").GetGuid());
        Assert.Equal(0, await ReceiptCountAsync(organizationId));
        Assert.Equal(0, await AllocationCountAsync(organizationId));
        await AssertOutstandingAsync(earlier.Id, 10m);
        await AssertOutstandingAsync(later.Id, 20m);
    }

    [Fact]
    public async Task Preview_excludes_ambiguous_candidates()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice first = PersistenceTestData.CreateInvoice(organizationId, 15m, "INV-AMB");
        Invoice second = PersistenceTestData.CreateInvoice(organizationId, 15m, "inv-amb");
        BankTransaction bank = PersistenceTestData.CreateBankTransaction(organizationId, 15m, reference: "INV-AMB");
        await SeedAsync(first, second, bank);
        using HttpClient client = ClientFor(organizationId);

        HttpResponseMessage response = await client.GetAsync(PreviewPath);

        using JsonDocument document = await ReadJsonAsync(response);
        JsonElement root = document.RootElement;
        Assert.Equal(0, root.GetProperty("eligibleCount").GetInt32());
        Assert.True(root.GetProperty("ambiguousGroupCount").GetInt32() >= 1);
        Assert.Empty(root.GetProperty("eligiblePairs").EnumerateArray());
        Assert.NotEmpty(root.GetProperty("ambiguousGroups").EnumerateArray());
        Assert.Equal(0, await AllocationCountAsync(organizationId));
    }

    [Fact]
    public async Task Preview_open_review_case_excludes_the_pair_and_a_dismissed_case_does_not()
    {
        OrganizationId blockedOrganization = OrganizationId.New();
        (Invoice blockedInvoice, BankTransaction blockedBank) = await SeedPairAsync(blockedOrganization, "REV-O", 8m);
        await OpenReviewAsync(blockedOrganization, blockedInvoice.Id, blockedBank.Id, dismiss: false);
        using HttpClient blockedClient = ClientFor(blockedOrganization);

        HttpResponseMessage blocked = await blockedClient.GetAsync(PreviewPath);

        using JsonDocument blockedDocument = await ReadJsonAsync(blocked);
        Assert.Equal(0, blockedDocument.RootElement.GetProperty("eligibleCount").GetInt32());
        Assert.Equal(1, blockedDocument.RootElement.GetProperty("excludedByReviewCount").GetInt32());

        OrganizationId closedOrganization = OrganizationId.New();
        (Invoice closedInvoice, BankTransaction closedBank) = await SeedPairAsync(closedOrganization, "REV-C", 8m);
        await OpenReviewAsync(closedOrganization, closedInvoice.Id, closedBank.Id, dismiss: true);
        using HttpClient closedClient = ClientFor(closedOrganization);

        HttpResponseMessage closed = await closedClient.GetAsync(PreviewPath);

        using JsonDocument closedDocument = await ReadJsonAsync(closed);
        Assert.Equal(1, closedDocument.RootElement.GetProperty("eligibleCount").GetInt32());
        Assert.Equal(0, closedDocument.RootElement.GetProperty("excludedByReviewCount").GetInt32());
        AssertPair(
            Assert.Single(closedDocument.RootElement.GetProperty("eligiblePairs").EnumerateArray()),
            closedInvoice,
            closedBank,
            8m);
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("page=-1")]
    [InlineData("page=abc")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=101")]
    [InlineData("page=1&page=2")]
    [InlineData("organizationId=11111111-1111-1111-1111-111111111111")]
    [InlineData("page=2147483647&pageSize=2")]
    public async Task Invalid_preview_query_returns_400(string query)
    {
        using HttpClient client = ClientFor(OrganizationId.New());

        HttpResponseMessage response = await client.GetAsync(PreviewPath + "?" + query);

        await AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            ReconciliationApplicationErrors.BatchInvalidQuery.Code);
    }

    [Fact]
    public async Task Preview_and_execute_report_the_candidate_limit_without_writing()
    {
        OrganizationId organizationId = OrganizationId.New();
        await InsertOpenInvoicesAsync(organizationId, ExactMatchCandidateLimits.MaxPerSide + 1);
        using HttpClient client = ClientFor(organizationId);

        HttpResponseMessage preview = await client.GetAsync(PreviewPath);
        HttpResponseMessage execute = await client.SendAsync(
            ExecuteRequest(PairJson(Guid.NewGuid(), Guid.NewGuid()), Guid.NewGuid()));

        await AssertProblemAsync(
            preview,
            HttpStatusCode.Conflict,
            ReconciliationApplicationErrors.BatchCandidateLimitExceeded.Code);
        string executeBody = await AssertProblemAsync(
            execute,
            HttpStatusCode.Conflict,
            ReconciliationApplicationErrors.BatchCandidateLimitExceeded.Code);
        AssertNoSensitiveDetails(executeBody);
        Assert.Equal(0, await ReceiptCountAsync(organizationId));
        Assert.Equal(0, await AllocationCountAsync(organizationId));
    }

    [Fact]
    public async Task Preview_and_execute_report_inconsistent_money_without_writing_or_leaking_sql()
    {
        OrganizationId organizationId = OrganizationId.New();
        await InsertInconsistentInvoiceAsync(organizationId);
        using HttpClient client = ClientFor(organizationId);

        HttpResponseMessage preview = await client.GetAsync(PreviewPath);
        HttpResponseMessage execute = await client.SendAsync(
            ExecuteRequest(PairJson(Guid.NewGuid(), Guid.NewGuid()), Guid.NewGuid()));

        string previewBody = await AssertProblemAsync(
            preview,
            HttpStatusCode.InternalServerError,
            ReconciliationApplicationErrors.InconsistentMatchMoney.Code);
        string executeBody = await AssertProblemAsync(
            execute,
            HttpStatusCode.InternalServerError,
            ReconciliationApplicationErrors.InconsistentMatchMoney.Code);
        AssertNoSensitiveDetails(previewBody);
        AssertNoSensitiveDetails(executeBody);
        Assert.Equal(0, await ReceiptCountAsync(organizationId));
        Assert.Equal(0, await AllocationCountAsync(organizationId));
    }

    [Theory]
    [MemberData(nameof(RejectedExecuteRequests))]
    public async Task Rejected_execute_requests_write_nothing(string json, string? idempotencyKey, bool duplicateKey, HttpStatusCode status, string code)
    {
        OrganizationId organizationId = OrganizationId.New();
        using HttpClient client = ClientFor(organizationId);
        HttpRequestMessage request = new(HttpMethod.Post, ExecutePath)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (idempotencyKey is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
            if (duplicateKey)
            {
                request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
            }
        }

        HttpResponseMessage response = await client.SendAsync(request);

        await AssertProblemAsync(response, status, code);
        Assert.Equal(0, await ReceiptCountAsync(organizationId));
        Assert.Equal(0, await AllocationCountAsync(organizationId));
    }

    [Fact]
    public async Task Wrong_content_type_and_an_oversized_body_write_nothing()
    {
        OrganizationId organizationId = OrganizationId.New();
        using HttpClient client = ClientFor(organizationId);
        HttpRequestMessage wrongType = new(HttpMethod.Post, ExecutePath)
        {
            Content = new StringContent(PairJson(Guid.NewGuid(), Guid.NewGuid()), Encoding.UTF8, "text/plain"),
        };
        wrongType.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString("D"));
        HttpRequestMessage oversized = new(HttpMethod.Post, ExecutePath)
        {
            Content = new ByteArrayContent(new byte[BatchMatchEndpoints.MaxJsonBodyBytes + 1]),
        };
        oversized.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        oversized.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString("D"));

        HttpResponseMessage typeResponse = await client.SendAsync(wrongType);
        HttpResponseMessage sizeResponse = await client.SendAsync(oversized);

        await AssertProblemAsync(
            typeResponse,
            HttpStatusCode.UnsupportedMediaType,
            ReconciliationApplicationErrors.BatchUnsupportedMediaType.Code);
        await AssertProblemAsync(
            sizeResponse,
            HttpStatusCode.RequestEntityTooLarge,
            ReconciliationApplicationErrors.BatchRequestTooLarge.Code);
        Assert.Equal(0, await ReceiptCountAsync(organizationId));
        Assert.Equal(0, await AllocationCountAsync(organizationId));
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("application/json; charset=UTF-8")]
    [InlineData("application/json; charset=\"utf-8\"")]
    public async Task Utf8_json_content_type_confirms_the_pair(string contentType)
    {
        OrganizationId organizationId = OrganizationId.New();
        (Invoice invoice, BankTransaction bank) = await SeedPairAsync(organizationId, "UTF", 16m);
        using HttpClient client = ClientFor(organizationId);

        HttpResponseMessage response = await client.SendAsync(
            ExecuteWithContentType(
                PairJson(invoice.Id.Value, bank.Id.Value),
                Guid.NewGuid(),
                contentType));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = await ReadJsonAsync(response);
        Assert.Equal("confirmed", Item(document, invoice.Id.Value).GetProperty("outcome").GetString());
        Assert.Equal(1, await AllocationCountAsync(organizationId));
        Assert.Equal(1, await ReceiptCountAsync(organizationId));
    }

    [Theory]
    [InlineData("application/json; charset=invalid-charset")]
    [InlineData("application/json; charset=utf-16")]
    [InlineData("application/json; charset=\"utf-8")]
    [InlineData("application/json; charset=utf-8; charset=utf-16")]
    [InlineData("application/json; charset=utf-8; charset=UTF-8")]
    public async Task Unsupported_content_type_returns_415_and_writes_nothing(string contentType)
    {
        OrganizationId organizationId = OrganizationId.New();
        (Invoice invoice, BankTransaction bank) = await SeedPairAsync(organizationId, "TYPE", 16m);
        using HttpClient client = ClientFor(organizationId);

        HttpResponseMessage response = await client.SendAsync(
            ExecuteWithContentType(
                PairJson(invoice.Id.Value, bank.Id.Value),
                Guid.NewGuid(),
                contentType));

        await AssertProblemAsync(
            response,
            HttpStatusCode.UnsupportedMediaType,
            ReconciliationApplicationErrors.BatchUnsupportedMediaType.Code);
        Assert.Equal(0, await ReceiptCountAsync(organizationId));
        Assert.Equal(0, await AllocationCountAsync(organizationId));
        await AssertOutstandingAsync(invoice.Id, 16m);
    }

    [Fact]
    public async Task Valid_pair_confirms_one_allocation_and_replay_stays_stable()
    {
        OrganizationId organizationId = OrganizationId.New();
        (Invoice invoice, BankTransaction bank) = await SeedPairAsync(organizationId, "ONE", 18m);
        Guid key = Guid.NewGuid();
        using HttpClient client = ClientFor(organizationId);

        string body = PairJson(invoice.Id.Value, bank.Id.Value);
        HttpResponseMessage created = await client.SendAsync(ExecuteRequest(body, key));
        HttpResponseMessage replayed = await client.SendAsync(ExecuteRequest(body, key));

        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replayed.StatusCode);
        using JsonDocument createdDocument = await ReadJsonAsync(created);
        using JsonDocument replayedDocument = await ReadJsonAsync(replayed);
        AssertConfirmed(createdDocument.RootElement, invoice, bank, 18m);
        Assert.Equal(
            Item(createdDocument, invoice.Id.Value).GetProperty("allocationId").GetGuid(),
            Item(replayedDocument, invoice.Id.Value).GetProperty("allocationId").GetGuid());
        Assert.Equal(1, await AllocationCountAsync(organizationId));
        Assert.Equal(BatchExecutionStatus.Completed, await ReceiptStatusAsync(organizationId, key));
    }

    [Fact]
    public async Task Equal_remainders_confirm_the_current_balance()
    {
        OrganizationId organizationId = OrganizationId.New();
        (Invoice invoice, BankTransaction bank) = await SeedPairAsync(organizationId, "PART", 20m);
        using HttpClient client = ClientFor(organizationId);
        HttpRequestMessage allocation = new(HttpMethod.Post, "/api/allocations")
        {
            Content = new StringContent(
                "{\"invoiceId\":\"" + invoice.Id.Value + "\",\"bankTransactionId\":\"" + bank.Id.Value + "\",\"amount\":{\"amount\":5,\"currency\":\"PLN\"}}",
                Encoding.UTF8,
                "application/json"),
        };
        allocation.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString("D"));
        HttpResponseMessage allocated = await client.SendAsync(allocation);
        Assert.Equal(HttpStatusCode.Created, allocated.StatusCode);

        HttpResponseMessage response = await client.SendAsync(
            ExecuteRequest(PairJson(invoice.Id.Value, bank.Id.Value), Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = await ReadJsonAsync(response);
        JsonElement item = Item(document, invoice.Id.Value);
        Assert.Equal("confirmed", item.GetProperty("outcome").GetString());
        Assert.Equal(15m, item.GetProperty("amount").GetDecimal());
        Assert.Equal("PLN", item.GetProperty("currency").GetString());
        await AssertOutstandingAsync(invoice.Id, 0m);
    }

    [Fact]
    public async Task Currency_mismatch_ambiguous_reference_and_open_review_are_not_confirmed()
    {
        OrganizationId currencyOrganization = OrganizationId.New();
        Invoice plnInvoice = new(InvoiceId.New(), currencyOrganization, "CUR", new Money(10m, PersistenceTestData.Pln));
        BankTransaction eurBank = new(
            BankTransactionId.New(),
            currencyOrganization,
            new Money(10m, new CurrencyCode("EUR")),
            new DateOnly(2026, 8, 24),
            "CUR");
        await SeedAsync(plnInvoice, eurBank);
        using HttpClient currencyClient = ClientFor(currencyOrganization);
        HttpResponseMessage currency = await currencyClient.SendAsync(
            ExecuteRequest(PairJson(plnInvoice.Id.Value, eurBank.Id.Value), Guid.NewGuid()));
        using JsonDocument currencyDocument = await ReadJsonAsync(currency);
        Assert.Equal("stale", Item(currencyDocument, plnInvoice.Id.Value).GetProperty("outcome").GetString());
        Assert.False(currencyDocument.RootElement.GetProperty("allConfirmed").GetBoolean());
        Assert.Equal(0, await AllocationCountAsync(currencyOrganization));

        OrganizationId ambiguousOrganization = OrganizationId.New();
        Invoice ambiguousInvoice = PersistenceTestData.CreateInvoice(ambiguousOrganization, 15m, "INV-AMB");
        Invoice competitor = PersistenceTestData.CreateInvoice(ambiguousOrganization, 15m, "inv-amb");
        BankTransaction ambiguousBank = PersistenceTestData.CreateBankTransaction(
            ambiguousOrganization,
            15m,
            reference: "INV-AMB");
        await SeedAsync(ambiguousInvoice, competitor, ambiguousBank);
        using HttpClient ambiguousClient = ClientFor(ambiguousOrganization);
        HttpResponseMessage ambiguous = await ambiguousClient.SendAsync(
            ExecuteRequest(PairJson(ambiguousInvoice.Id.Value, ambiguousBank.Id.Value), Guid.NewGuid()));
        using JsonDocument ambiguousDocument = await ReadJsonAsync(ambiguous);
        Assert.Equal("skippedAmbiguous", Item(ambiguousDocument, ambiguousInvoice.Id.Value).GetProperty("outcome").GetString());
        Assert.Equal(0, await AllocationCountAsync(ambiguousOrganization));

        OrganizationId reviewOrganization = OrganizationId.New();
        (Invoice reviewInvoice, BankTransaction reviewBank) = await SeedPairAsync(reviewOrganization, "REV-X", 6m);
        await OpenReviewAsync(reviewOrganization, reviewInvoice.Id, reviewBank.Id, dismiss: false);
        using HttpClient reviewClient = ClientFor(reviewOrganization);
        HttpResponseMessage review = await reviewClient.SendAsync(
            ExecuteRequest(PairJson(reviewInvoice.Id.Value, reviewBank.Id.Value), Guid.NewGuid()));
        using JsonDocument reviewDocument = await ReadJsonAsync(review);
        Assert.Equal("skippedReview", Item(reviewDocument, reviewInvoice.Id.Value).GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, Item(reviewDocument, reviewInvoice.Id.Value).GetProperty("allocationId").ValueKind);
        Assert.Equal(0, await AllocationCountAsync(reviewOrganization));
    }

    [Fact]
    public async Task Mixed_and_fully_rejected_batches_report_the_result_counters()
    {
        OrganizationId mixedOrganization = OrganizationId.New();
        (Invoice good, BankTransaction goodBank) = await SeedPairAsync(mixedOrganization, "MIX-G", 7m);
        Invoice staleInvoice = PersistenceTestData.CreateInvoice(mixedOrganization, 4m, "MIX-S");
        BankTransaction staleBank = PersistenceTestData.CreateBankTransaction(mixedOrganization, 9m, reference: "MIX-S");
        await SeedAsync(staleInvoice, staleBank);
        using HttpClient mixedClient = ClientFor(mixedOrganization);
        HttpResponseMessage mixed = await mixedClient.SendAsync(ExecuteRequest(
            PairJson(good.Id.Value, goodBank.Id.Value, staleInvoice.Id.Value, staleBank.Id.Value),
            Guid.NewGuid()));
        using JsonDocument mixedDocument = await ReadJsonAsync(mixed);
        JsonElement mixedRoot = mixedDocument.RootElement;
        Assert.Equal(2, mixedRoot.GetProperty("requestedCount").GetInt32());
        Assert.Equal(1, mixedRoot.GetProperty("confirmedCount").GetInt32());
        Assert.Equal(1, mixedRoot.GetProperty("rejectedCount").GetInt32());
        Assert.Equal(0, mixedRoot.GetProperty("interruptedCount").GetInt32());
        Assert.False(mixedRoot.GetProperty("allConfirmed").GetBoolean());
        Assert.True(mixedRoot.GetProperty("hasRejections").GetBoolean());
        Assert.True(mixedRoot.GetProperty("partial").GetBoolean());
        Assert.Equal("confirmed", Item(mixedDocument, good.Id.Value).GetProperty("outcome").GetString());
        Assert.Equal("stale", Item(mixedDocument, staleInvoice.Id.Value).GetProperty("outcome").GetString());

        OrganizationId rejectedOrganization = OrganizationId.New();
        Invoice first = PersistenceTestData.CreateInvoice(rejectedOrganization, 3m, "NO-1");
        BankTransaction firstBank = PersistenceTestData.CreateBankTransaction(rejectedOrganization, 8m, reference: "NO-1");
        Invoice second = PersistenceTestData.CreateInvoice(rejectedOrganization, 3m, "NO-2");
        BankTransaction secondBank = PersistenceTestData.CreateBankTransaction(rejectedOrganization, 8m, reference: "NO-2");
        await SeedAsync(first, firstBank, second, secondBank);
        using HttpClient rejectedClient = ClientFor(rejectedOrganization);
        HttpResponseMessage rejected = await rejectedClient.SendAsync(ExecuteRequest(
            PairJson(first.Id.Value, firstBank.Id.Value, second.Id.Value, secondBank.Id.Value),
            Guid.NewGuid()));
        using JsonDocument rejectedDocument = await ReadJsonAsync(rejected);
        JsonElement rejectedRoot = rejectedDocument.RootElement;
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
        Assert.Equal(0, rejectedRoot.GetProperty("confirmedCount").GetInt32());
        Assert.Equal(2, rejectedRoot.GetProperty("rejectedCount").GetInt32());
        Assert.False(rejectedRoot.GetProperty("allConfirmed").GetBoolean());
        Assert.True(rejectedRoot.GetProperty("hasRejections").GetBoolean());
        Assert.False(rejectedRoot.GetProperty("partial").GetBoolean());
        Assert.Equal(0, await AllocationCountAsync(rejectedOrganization));
    }

    [Fact]
    public async Task Reordered_pairs_replay_and_a_different_pair_conflicts()
    {
        OrganizationId organizationId = OrganizationId.New();
        (Invoice first, BankTransaction firstBank) = await SeedPairAsync(organizationId, "ORD-A", 5m);
        (Invoice second, BankTransaction secondBank) = await SeedPairAsync(organizationId, "ORD-B", 6m);
        Guid key = Guid.NewGuid();
        using HttpClient client = ClientFor(organizationId);
        string original = PairJson(second.Id.Value, secondBank.Id.Value, first.Id.Value, firstBank.Id.Value);
        string reordered = PairJson(first.Id.Value, firstBank.Id.Value, second.Id.Value, secondBank.Id.Value);

        HttpResponseMessage created = await client.SendAsync(ExecuteRequest(original, key));
        HttpResponseMessage replayed = await client.SendAsync(ExecuteRequest(reordered, key));
        HttpResponseMessage conflict = await client.SendAsync(
            ExecuteRequest(PairJson(first.Id.Value, firstBank.Id.Value), key));

        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replayed.StatusCode);
        using JsonDocument createdDocument = await ReadJsonAsync(created);
        using JsonDocument replayedDocument = await ReadJsonAsync(replayed);
        Assert.Equal(
            Item(createdDocument, first.Id.Value).GetProperty("allocationId").GetGuid(),
            Item(replayedDocument, first.Id.Value).GetProperty("allocationId").GetGuid());
        AssertCanonicalInvoiceOrder(replayedDocument.RootElement);
        await AssertProblemAsync(
            conflict,
            HttpStatusCode.Conflict,
            ReconciliationApplicationErrors.BatchIdempotencyPayloadConflict.Code);
        Assert.Equal(2, await AllocationCountAsync(organizationId));
    }

    [Fact]
    public async Task Completed_replay_keeps_the_stored_result_after_a_later_review_case()
    {
        OrganizationId organizationId = OrganizationId.New();
        (Invoice invoice, BankTransaction bank) = await SeedPairAsync(organizationId, "FREEZE", 13m);
        Guid key = Guid.NewGuid();
        using HttpClient client = ClientFor(organizationId);
        HttpResponseMessage created = await client.SendAsync(
            ExecuteRequest(PairJson(invoice.Id.Value, bank.Id.Value), key));
        using JsonDocument createdDocument = await ReadJsonAsync(created);
        Guid allocationId = Item(createdDocument, invoice.Id.Value).GetProperty("allocationId").GetGuid();
        await OpenReviewAsync(organizationId, invoice.Id, bank.Id, dismiss: false);

        HttpResponseMessage replayed = await client.SendAsync(
            ExecuteRequest(PairJson(invoice.Id.Value, bank.Id.Value), key));

        using JsonDocument replayedDocument = await ReadJsonAsync(replayed);
        JsonElement item = Item(replayedDocument, invoice.Id.Value);
        Assert.Equal("confirmed", item.GetProperty("outcome").GetString());
        Assert.Equal(allocationId, item.GetProperty("allocationId").GetGuid());
        Assert.Equal(13m, item.GetProperty("amount").GetDecimal());
        Assert.True(replayedDocument.RootElement.GetProperty("allConfirmed").GetBoolean());
        Assert.Equal(1, await AllocationCountAsync(organizationId));
    }

    [Fact]
    public async Task Simultaneous_identical_requests_let_one_owner_finish()
    {
        OrganizationId organizationId = OrganizationId.New();
        (Invoice invoice, BankTransaction bank) = await SeedPairAsync(organizationId, "RACE", 21m);
        Guid key = Guid.NewGuid();
        HoldingSynchronization hold = new();
        await using var factory = new FinOpsDeskApiFactory(
            _fixture.ConnectionString,
            services =>
            {
                services.RemoveAll<IBatchExactMatchItemSynchronization>();
                services.AddSingleton<IBatchExactMatchItemSynchronization>(hold);
            });
        using HttpClient client = factory.CreateApiClient(TestAccessTokens.Create(organizationId.Value));
        using HttpRequestMessage firstRequest = ExecuteRequest(PairJson(invoice.Id.Value, bank.Id.Value), key);
        using HttpRequestMessage secondRequest = ExecuteRequest(PairJson(invoice.Id.Value, bank.Id.Value), key);

        Task<HttpResponseMessage> first = client.SendAsync(firstRequest);
        await hold.Entered.WaitAsync(TimeSpan.FromSeconds(20));
        HttpResponseMessage second = await client.SendAsync(secondRequest);
        string secondBody = await AssertProblemAsync(
            second,
            HttpStatusCode.Conflict,
            ReconciliationApplicationErrors.BatchExecutionInProgress.Code);
        AssertNoSensitiveDetails(secondBody);
        hold.Release();
        HttpResponseMessage completed = await first;

        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        Assert.Equal(1, await AllocationCountAsync(organizationId));
        Assert.Equal(BatchExecutionStatus.Completed, await ReceiptStatusAsync(organizationId, key));
    }

    [Fact]
    public async Task Cancelling_before_commit_then_retrying_allocates_once()
    {
        OrganizationId organizationId = OrganizationId.New();
        (Invoice invoice, BankTransaction bank) = await SeedPairAsync(organizationId, "CANCEL", 22m);
        Guid key = Guid.NewGuid();
        HoldingSynchronization hold = new();
        TaskCompletionSource disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var factory = new FinOpsDeskApiFactory(
            _fixture.ConnectionString,
            services =>
            {
                services.RemoveAll<IBatchExactMatchItemSynchronization>();
                services.AddSingleton<IBatchExactMatchItemSynchronization>(hold);
                services.AddScoped<IBatchExecutionOwner>(provider =>
                    new ReleaseSignalingOwner(
                        ActivatorUtilities.CreateInstance<BatchExecutionOwner>(provider),
                        disposed));
            });
        using HttpClient client = factory.CreateApiClient(TestAccessTokens.Create(organizationId.Value));
        using var cancellation = new CancellationTokenSource();
        using HttpRequestMessage request = ExecuteRequest(PairJson(invoice.Id.Value, bank.Id.Value), key);

        Task<HttpResponseMessage> running = client.SendAsync(request, cancellation.Token);
        await hold.Entered.WaitAsync(TimeSpan.FromSeconds(20));
        await cancellation.CancelAsync();
        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await running);
        Assert.Equal(0, await AllocationCountAsync(organizationId));
        Assert.Equal(BatchExecutionStatus.Running, await ReceiptStatusAsync(organizationId, key));

        using HttpClient retryClient = ClientFor(organizationId);
        HttpResponseMessage recovered = await retryClient.SendAsync(
            ExecuteRequest(PairJson(invoice.Id.Value, bank.Id.Value), key));

        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        using JsonDocument document = await ReadJsonAsync(recovered);
        Assert.Equal("confirmed", Item(document, invoice.Id.Value).GetProperty("outcome").GetString());
        Assert.Equal(1, await AllocationCountAsync(organizationId));
        Assert.Equal(BatchExecutionStatus.Completed, await ReceiptStatusAsync(organizationId, key));
    }

    [Fact]
    public async Task Allocation_identity_conflict_returns_409_without_another_allocation()
    {
        OrganizationId organizationId = OrganizationId.New();
        (Invoice invoice, BankTransaction bank) = await SeedPairAsync(organizationId, "IDENT", 14m);
        Invoice decoyInvoice = PersistenceTestData.CreateInvoice(organizationId, 9876.54m, "DECOY");
        BankTransaction decoyBank = PersistenceTestData.CreateBankTransaction(organizationId, 9876.54m, reference: "DECOY");
        await SeedAsync(decoyInvoice, decoyBank);
        Guid batchKey = Guid.NewGuid();
        Guid itemKey = BatchExactMatchItemKey.Derive(organizationId, batchKey, invoice.Id, bank.Id);
        await using ReconciliationDbContext context = _fixture.CreateContext();
        context.Allocations.Add(new Allocation(
            AllocationId.New(),
            organizationId,
            decoyInvoice.Id,
            decoyBank.Id,
            new Money(9876.54m, PersistenceTestData.Pln),
            DateTimeOffset.UtcNow,
            itemKey,
            AllocationIdempotencyOperation.BatchExactMatch));
        await context.SaveChangesAsync();
        using HttpClient client = ClientFor(organizationId);

        HttpResponseMessage response = await client.SendAsync(
            ExecuteRequest(PairJson(invoice.Id.Value, bank.Id.Value), batchKey));

        string body = await AssertProblemAsync(
            response,
            HttpStatusCode.Conflict,
            ReconciliationApplicationErrors.BatchAllocationIntegrityConflict.Code);
        AssertNoSensitiveDetails(body);
        Assert.DoesNotContain("9876.54", body, StringComparison.Ordinal);
        Assert.DoesNotContain(decoyInvoice.Id.Value.ToString("D"), body, StringComparison.OrdinalIgnoreCase);
        await AssertOutstandingAsync(invoice.Id, 14m);
        Assert.Equal(1, await AllocationCountAsync(organizationId));
    }

    [Fact]
    public async Task OpenApi_document_describes_the_batch_routes_and_runtime_authorization()
    {
        using HttpClient client = ClientFor(OrganizationId.New());

        HttpResponseMessage response = await client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = await ReadJsonAsync(response);
        JsonElement paths = document.RootElement.GetProperty("paths");
        JsonElement preview = paths.GetProperty(PreviewPath).GetProperty("get");
        JsonElement execute = paths.GetProperty(ExecutePath).GetProperty("post");
        Assert.Equal("PreviewBatchExactMatches", preview.GetProperty("operationId").GetString());
        Assert.Equal("ExecuteBatchExactMatches", execute.GetProperty("operationId").GetString());
        Assert.Contains("TenantMember", preview.GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Contains("TenantMember", execute.GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Contains("Phase 7", execute.GetProperty("description").GetString(), StringComparison.Ordinal);
        AssertNotAnonymous(preview);
        AssertNotAnonymous(execute);

        JsonElement page = Parameter(preview, "page");
        JsonElement pageSize = Parameter(preview, "pageSize");
        Assert.Equal(1, page.GetProperty("schema").GetProperty("default").GetInt32());
        Assert.Equal(50, pageSize.GetProperty("schema").GetProperty("default").GetInt32());
        Assert.Equal(100, pageSize.GetProperty("schema").GetProperty("maximum").GetInt32());
        Assert.True(preview.GetProperty("responses").TryGetProperty("200", out _));
        Assert.True(preview.GetProperty("responses").TryGetProperty("400", out _));
        Assert.True(preview.GetProperty("responses").TryGetProperty("401", out _));
        Assert.True(preview.GetProperty("responses").TryGetProperty("403", out _));
        Assert.True(preview.GetProperty("responses").TryGetProperty("409", out _));
        Assert.True(preview.GetProperty("responses").TryGetProperty("500", out _));

        JsonElement idempotency = Parameter(execute, "Idempotency-Key");
        Assert.True(idempotency.GetProperty("required").GetBoolean());
        Assert.Equal("header", idempotency.GetProperty("in").GetString());
        JsonElement requestSchema = execute.GetProperty("requestBody")
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema");
        Assert.Equal("object", SchemaType(requestSchema));
        Assert.False(requestSchema.GetProperty("additionalProperties").GetBoolean());
        Assert.Contains("pairs", RequiredProperties(requestSchema));
        JsonElement pairs = requestSchema.GetProperty("properties").GetProperty("pairs");
        Assert.Equal(1, pairs.GetProperty("minItems").GetInt32());
        Assert.Equal(25, pairs.GetProperty("maxItems").GetInt32());
        Assert.True(execute.GetProperty("responses").TryGetProperty("200", out _));
        Assert.True(execute.GetProperty("responses").TryGetProperty("400", out _));
        Assert.True(execute.GetProperty("responses").TryGetProperty("401", out _));
        Assert.True(execute.GetProperty("responses").TryGetProperty("403", out _));
        Assert.True(execute.GetProperty("responses").TryGetProperty("409", out _));
        Assert.True(execute.GetProperty("responses").TryGetProperty("413", out _));
        Assert.True(execute.GetProperty("responses").TryGetProperty("415", out _));
        Assert.True(execute.GetProperty("responses").TryGetProperty("500", out _));

        JsonElement itemSchema = document.RootElement
            .GetProperty("components")
            .GetProperty("schemas")
            .GetProperty(nameof(ExecuteBatchExactMatchItemResponse));
        AssertEnum(
            itemSchema.GetProperty("properties").GetProperty("outcome"),
            "confirmed",
            "skippedAmbiguous",
            "skippedCoverage",
            "skippedReview",
            "stale",
            "conflict",
            "notFound",
            "interrupted");
    }

    public static IEnumerable<object?[]> RejectedExecuteRequests()
    {
        Guid invoiceId = Guid.NewGuid();
        Guid bankId = Guid.NewGuid();
        string validPair = PairJson(invoiceId, bankId);
        string validKey = Guid.NewGuid().ToString("D");
        yield return Row("{}", null, false, HttpStatusCode.BadRequest, ReconciliationApplicationErrors.IdempotencyKeyRequired.Code);
        yield return Row(validPair, "   ", false, HttpStatusCode.BadRequest, ReconciliationApplicationErrors.IdempotencyKeyRequired.Code);
        yield return Row(validPair, validKey, true, HttpStatusCode.BadRequest, ReconciliationApplicationErrors.IdempotencyKeyInvalid.Code);
        yield return Row(validPair, "not-a-guid", false, HttpStatusCode.BadRequest, ReconciliationApplicationErrors.IdempotencyKeyInvalid.Code);
        yield return Row(validPair, Guid.NewGuid().ToString("N"), false, HttpStatusCode.BadRequest, ReconciliationApplicationErrors.IdempotencyKeyInvalid.Code);
        yield return Row(validPair, Guid.Empty.ToString("D"), false, HttpStatusCode.BadRequest, ReconciliationApplicationErrors.IdempotencyKeyInvalid.Code);
        yield return Row("{", validKey, false, HttpStatusCode.BadRequest, ReconciliationApplicationErrors.BatchInvalidRequest.Code);
        yield return Row(validPair + "{}", validKey, false, HttpStatusCode.BadRequest, ReconciliationApplicationErrors.BatchInvalidRequest.Code);
        yield return Row("""{"pairs":[],"organizationId":"11111111-1111-1111-1111-111111111111"}""", validKey, false, HttpStatusCode.BadRequest, ReconciliationApplicationErrors.BatchInvalidRequest.Code);
        yield return Row("""{"pairs":[],"Pairs":[]}""", validKey, false, HttpStatusCode.BadRequest, ReconciliationApplicationErrors.BatchInvalidRequest.Code);
        yield return Row("{}", validKey, false, HttpStatusCode.BadRequest, ReconciliationApplicationErrors.BatchInvalidRequest.Code);
        yield return Row("""{"pairs":null}""", validKey, false, HttpStatusCode.BadRequest, ReconciliationApplicationErrors.BatchInvalidRequest.Code);
        yield return Row("""{"pairs":[]}""", validKey, false, HttpStatusCode.BadRequest, ReconciliationApplicationErrors.BatchInvalidRequest.Code);
        yield return Row(ManyPairs(26), validKey, false, HttpStatusCode.BadRequest, ReconciliationApplicationErrors.BatchInvalidRequest.Code);
        yield return Row(PairJson(invoiceId, bankId, invoiceId, bankId), validKey, false, HttpStatusCode.BadRequest, ReconciliationApplicationErrors.BatchInvalidRequest.Code);
        yield return Row(PairJson(Guid.Empty, bankId), validKey, false, HttpStatusCode.BadRequest, ReconciliationApplicationErrors.BatchInvalidRequest.Code);
        yield return Row(PairJson(invoiceId, Guid.Empty), validKey, false, HttpStatusCode.BadRequest, ReconciliationApplicationErrors.BatchInvalidRequest.Code);
        yield return Row(
            $$"""{"pairs":[{"invoiceId":"{{invoiceId}}","bankTransactionId":"{{bankId}}","amount":1,"currency":"PLN"}]}""",
            validKey,
            false,
            HttpStatusCode.BadRequest,
            ReconciliationApplicationErrors.BatchInvalidRequest.Code);
        yield return Row("""{"pairs":[[]]}""", validKey, false, HttpStatusCode.BadRequest, ReconciliationApplicationErrors.BatchInvalidRequest.Code);
        yield return Row("""{"pairs":[{"invoiceId":1,"bankTransactionId":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"}]}""", validKey, false, HttpStatusCode.BadRequest, ReconciliationApplicationErrors.BatchInvalidRequest.Code);
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    private static object?[] Row(
        string json,
        string? idempotencyKey,
        bool duplicateKey,
        HttpStatusCode status,
        string code)
    {
        return [json, idempotencyKey, duplicateKey, status, code];
    }

    private HttpClient ClientFor(OrganizationId organizationId)
    {
        return _factory.CreateApiClient(TestAccessTokens.Create(organizationId.Value));
    }

    private async Task<(Invoice Invoice, BankTransaction Bank)> SeedPairAsync(
        OrganizationId organizationId,
        string reference,
        decimal amount)
    {
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, amount, reference);
        BankTransaction bank = PersistenceTestData.CreateBankTransaction(organizationId, amount, reference: reference);
        await SeedAsync(invoice, bank);
        return (invoice, bank);
    }

    private async Task SeedAsync(params object[] entities)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        context.AddRange(entities);
        await context.SaveChangesAsync();
    }

    private async Task OpenReviewAsync(
        OrganizationId organizationId,
        InvoiceId invoiceId,
        BankTransactionId bankTransactionId,
        bool dismiss)
    {
        ReviewCase reviewCase = ReviewCase.Open(
            ReviewCaseId.New(),
            organizationId,
            ReviewCaseReason.CompetingMatch,
            invoiceId,
            bankTransactionId,
            ReviewCaseSource.Manual,
            note: null,
            DateTimeOffset.UtcNow);
        if (dismiss)
        {
            Assert.True(reviewCase.Dismiss(null, DateTimeOffset.UtcNow).IsSuccess);
        }

        await using ReconciliationDbContext context = _fixture.CreateContext();
        context.ReviewCases.Add(reviewCase);
        await context.SaveChangesAsync();
    }

    private async Task InsertOpenInvoicesAsync(OrganizationId organizationId, int count)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            WITH numbers AS (
                SELECT TOP ({count}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n
                FROM sys.all_objects AS leftSide
                CROSS JOIN sys.all_objects AS rightSide
            )
            INSERT INTO [reconciliation].[Invoices]
                ([Id], [OrganizationId], [InvoiceNumber], [AllocatedAmount], [AllocatedCurrency],
                 [TotalAmount], [TotalCurrency])
            SELECT NEWID(), {organizationId.Value}, CONCAT({"LIMIT-"}, numbers.n),
                   {0m}, {"PLN"}, {3m}, {"PLN"}
            FROM numbers;
            """);
    }

    private async Task InsertInconsistentInvoiceAsync(OrganizationId organizationId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO [reconciliation].[Invoices]
                ([Id], [OrganizationId], [InvoiceNumber], [AllocatedAmount], [AllocatedCurrency],
                 [TotalAmount], [TotalCurrency])
            VALUES
                ({Guid.NewGuid()}, {organizationId.Value}, {"CORRUPT"}, {0m}, {"USD"}, {10m}, {"PLN"})
            """);
    }

    private async Task<int> AllocationCountAsync(OrganizationId organizationId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        return await context.Allocations.CountAsync(allocation => allocation.OrganizationId == organizationId);
    }

    private async Task<int> ReceiptCountAsync(OrganizationId organizationId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        return await context.BatchExecutions.CountAsync(
            execution => execution.OrganizationId == organizationId);
    }

    private async Task<BatchExecutionStatus> ReceiptStatusAsync(OrganizationId organizationId, Guid key)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        return await context.BatchExecutions
            .Where(execution => execution.OrganizationId == organizationId && execution.IdempotencyKey == key)
            .Select(execution => execution.Status)
            .SingleAsync();
    }

    private async Task AssertOutstandingAsync(InvoiceId invoiceId, decimal amount)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Invoice invoice = await context.Invoices.SingleAsync(candidate => candidate.Id == invoiceId);
        Assert.Equal(amount, invoice.OutstandingAmount.Amount);
    }

    private static HttpRequestMessage ExecuteRequest(string json, Guid idempotencyKey)
    {
        HttpRequestMessage request = new(HttpMethod.Post, ExecutePath)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey.ToString("D"));
        return request;
    }

    private static HttpRequestMessage ExecuteWithContentType(
        string json,
        Guid idempotencyKey,
        string contentType)
    {
        HttpRequestMessage request = new(HttpMethod.Post, ExecutePath)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(json)),
        };
        request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey.ToString("D"));
        return request;
    }

    private static string PairJson(Guid invoiceId, Guid bankTransactionId)
    {
        return $$"""{"pairs":[{"invoiceId":"{{invoiceId}}","bankTransactionId":"{{bankTransactionId}}"}]}""";
    }

    private static string PairJson(Guid firstInvoice, Guid firstBank, Guid secondInvoice, Guid secondBank)
    {
        return $$"""{"pairs":[{"invoiceId":"{{firstInvoice}}","bankTransactionId":"{{firstBank}}"},{"invoiceId":"{{secondInvoice}}","bankTransactionId":"{{secondBank}}"}]}""";
    }

    private static string ManyPairs(int count)
    {
        StringBuilder builder = new();
        builder.Append("{\"pairs\":[");
        for (int index = 0; index < count; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }

            builder.Append("{\"invoiceId\":\"")
                .Append(Guid.NewGuid())
                .Append("\",\"bankTransactionId\":\"")
                .Append(Guid.NewGuid())
                .Append("\"}");
        }

        builder.Append("]}");
        return builder.ToString();
    }

    private static void AssertPair(
        JsonElement pair,
        Invoice invoice,
        BankTransaction bank,
        decimal amount)
    {
        Assert.Equal(invoice.Id.Value, pair.GetProperty("invoiceId").GetGuid());
        Assert.Equal(bank.Id.Value, pair.GetProperty("bankTransactionId").GetGuid());
        Assert.Equal(amount, pair.GetProperty("amount").GetProperty("amount").GetDecimal());
        Assert.Equal("PLN", pair.GetProperty("amount").GetProperty("currency").GetString());
        Assert.False(string.IsNullOrWhiteSpace(pair.GetProperty("normalizedReference").GetString()));
    }

    private static void AssertConfirmed(
        JsonElement root,
        Invoice invoice,
        BankTransaction bank,
        decimal amount)
    {
        Assert.Equal(1, root.GetProperty("resultVersion").GetInt32());
        Assert.Equal(1, root.GetProperty("requestedCount").GetInt32());
        Assert.Equal(1, root.GetProperty("confirmedCount").GetInt32());
        Assert.Equal(0, root.GetProperty("rejectedCount").GetInt32());
        Assert.Equal(0, root.GetProperty("interruptedCount").GetInt32());
        Assert.True(root.GetProperty("allConfirmed").GetBoolean());
        Assert.False(root.GetProperty("hasRejections").GetBoolean());
        Assert.False(root.GetProperty("partial").GetBoolean());
        JsonElement item = Assert.Single(root.GetProperty("items").EnumerateArray());
        Assert.Equal(invoice.Id.Value, item.GetProperty("invoiceId").GetGuid());
        Assert.Equal(bank.Id.Value, item.GetProperty("bankTransactionId").GetGuid());
        Assert.Equal("confirmed", item.GetProperty("outcome").GetString());
        Assert.NotEqual(Guid.Empty, item.GetProperty("allocationId").GetGuid());
        Assert.Equal(amount, item.GetProperty("amount").GetDecimal());
        Assert.Equal("PLN", item.GetProperty("currency").GetString());
    }

    private static JsonElement Item(JsonDocument document, Guid invoiceId)
    {
        return document.RootElement.GetProperty("items").EnumerateArray()
            .Single(item => item.GetProperty("invoiceId").GetGuid() == invoiceId);
    }

    private static void AssertCanonicalInvoiceOrder(JsonElement root)
    {
        string? previous = null;
        foreach (JsonElement item in root.GetProperty("items").EnumerateArray())
        {
            string current = item.GetProperty("invoiceId").GetGuid().ToString("D");
            if (previous is not null)
            {
                Assert.True(string.CompareOrdinal(previous, current) < 0);
            }

            previous = current;
        }
    }

    private static async Task<string> AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode statusCode,
        string code)
    {
        Assert.Equal(statusCode, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(body);
        Assert.Equal((int)statusCode, document.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("detail").GetString()));
        return body;
    }

    private static void AssertNoSensitiveDetails(string body)
    {
        Assert.DoesNotContain("UPDLOCK", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ROWLOCK", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SqlException", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Server=", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Password=", body, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        string json = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(json);
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

    private static void AssertNotAnonymous(JsonElement operation)
    {
        if (operation.TryGetProperty("security", out JsonElement security)
            && security.ValueKind == JsonValueKind.Array)
        {
            Assert.NotEmpty(security.EnumerateArray());
        }
    }

    private sealed class HoldingSynchronization : IBatchExactMatchItemSynchronization
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public void Release()
        {
            _release.TrySetResult();
        }

        public async Task OnItemReadyToCommitAsync(Guid itemKey, CancellationToken cancellationToken)
        {
            _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class ReleaseSignalingOwner : IBatchExecutionOwner
    {
        private readonly BatchExecutionOwner _inner;
        private readonly TaskCompletionSource _disposed;

        public ReleaseSignalingOwner(BatchExecutionOwner inner, TaskCompletionSource disposed)
        {
            _inner = inner;
            _disposed = disposed;
        }

        public async Task<Result<IBatchExecutionLease>> TryAcquireAsync(
            OrganizationId organizationId,
            Guid idempotencyKey,
            CancellationToken cancellationToken)
        {
            Result<IBatchExecutionLease> acquired = await _inner.TryAcquireAsync(
                organizationId,
                idempotencyKey,
                cancellationToken);

            if (acquired.IsFailure)
            {
                return acquired;
            }

            return Result<IBatchExecutionLease>.Success(new SignalingLease(acquired.Value!, _disposed));
        }
    }

    private sealed class SignalingLease : IBatchExecutionLease
    {
        private readonly IBatchExecutionLease _inner;
        private readonly TaskCompletionSource _disposed;

        public SignalingLease(IBatchExecutionLease inner, TaskCompletionSource disposed)
        {
            _inner = inner;
            _disposed = disposed;
        }

        public BatchExecutionSnapshot Snapshot => _inner.Snapshot;

        public int OwnerSessionId => _inner.OwnerSessionId;

        public Task<bool> TryCompleteAsync(
            BatchExecutionResult result,
            DateTimeOffset completedAt,
            CancellationToken cancellationToken)
        {
            return _inner.TryCompleteAsync(result, completedAt, cancellationToken);
        }

        public Task CommitAsync(CancellationToken cancellationToken)
        {
            return _inner.CommitAsync(cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync();
            _disposed.TrySetResult();
        }
    }
}