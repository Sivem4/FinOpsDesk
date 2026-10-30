using System.Net;
using System.Text.Json;

using FinOpsDesk.IntegrationTests.Reconciliation.Persistence;
using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Imports;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;

namespace FinOpsDesk.IntegrationTests.Api;

[Collection(SqlServerCollection.Name)]
public sealed class BankTransactionEndpointTests : IDisposable
{
    private readonly SqlServerFixture _fixture;
    private readonly FinOpsDeskApiFactory _factory;

    public BankTransactionEndpointTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
        _factory = new FinOpsDeskApiFactory(fixture.ConnectionString);
    }

    [Fact]
    public async Task Empty_organization_returns_an_empty_page()
    {
        using HttpClient client = Client(OrganizationId.New());

        using JsonDocument document = await ReadOkAsync(await client.GetAsync("/api/bank-transactions"));

        JsonElement root = document.RootElement;
        Assert.Equal(1, root.GetProperty("page").GetInt32());
        Assert.Equal(50, root.GetProperty("pageSize").GetInt32());
        Assert.Equal(0, root.GetProperty("totalCount").GetInt32());
        Assert.Empty(root.GetProperty("items").EnumerateArray());
        Assert.False(root.TryGetProperty("organizationId", out _));
    }

    [Fact]
    public async Task Get_bank_transactions_returns_current_balances_for_the_token_organization()
    {
        OrganizationId organizationA = OrganizationId.New();
        OrganizationId organizationB = OrganizationId.New();
        BankTransaction unallocated = Create(
            organizationA,
            10.2501m,
            new DateOnly(2026, 3, 15),
            reference: null);
        BankTransaction partial = Create(organizationA, 100m, new DateOnly(2026, 3, 16), "PAY-PARTIAL");
        partial.Allocate(new Money(25.10m, PersistenceTestData.Pln));
        BankTransaction allocated = Create(organizationA, 40m, new DateOnly(2026, 3, 17), "PAY-FULL");
        allocated.Allocate(new Money(40m, PersistenceTestData.Pln));
        BankTransaction reversed = Create(organizationA, 80m, new DateOnly(2026, 3, 18), "PAY-REVERSED");
        reversed.Allocate(new Money(80m, PersistenceTestData.Pln));
        reversed.ReverseAllocation(new Money(80m, PersistenceTestData.Pln));
        BankTransaction sourced = new(
            BankTransactionId.New(),
            organizationA,
            new Money(15m, PersistenceTestData.Pln),
            new DateOnly(2026, 4, 1),
            "PAY-SOURCE",
            new SourceIdentity(new SourceSystem("bank_csv"), new ExternalId("TX-123")));
        BankTransaction otherTenant = Create(organizationB, 999m, new DateOnly(2026, 1, 1), "PAY-OTHER");
        await SaveAsync(unallocated, partial, allocated, reversed, sourced, otherTenant);

        using HttpClient client = Client(organizationA);
        string json = await (await client.GetAsync("/api/bank-transactions")).Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        Assert.Equal(5, root.GetProperty("totalCount").GetInt32());
        Assert.False(root.TryGetProperty("organizationId", out _));
        Assert.Equal(5, root.GetProperty("items").GetArrayLength());
        Assert.DoesNotContain("PAY-OTHER", json, StringComparison.Ordinal);
        Assert.Contains("10.2501", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"10.2501\"", json, StringComparison.Ordinal);
        Assert.Contains("\"bookingDate\":\"2026-03-15\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("T00:00", json, StringComparison.Ordinal);

        JsonElement open = Find(root, unallocated.Id.Value);
        Assert.Equal(JsonValueKind.Null, open.GetProperty("reference").ValueKind);
        Assert.Equal(JsonValueKind.Null, open.GetProperty("sourceSystem").ValueKind);
        Assert.Equal(JsonValueKind.Null, open.GetProperty("externalId").ValueKind);
        Assert.False(open.TryGetProperty("organizationId", out _));
        Assert.False(open.TryGetProperty("rowVersion", out _));
        AssertMoney(open.GetProperty("amount"), 10.2501m, "PLN");
        AssertMoney(open.GetProperty("allocatedAmount"), 0m, "PLN");
        AssertMoney(open.GetProperty("unappliedAmount"), 10.2501m, "PLN");

        JsonElement partialItem = Find(root, partial.Id.Value);
        AssertMoney(partialItem.GetProperty("allocatedAmount"), 25.10m, "PLN");
        AssertMoney(partialItem.GetProperty("unappliedAmount"), 74.90m, "PLN");

        JsonElement fullItem = Find(root, allocated.Id.Value);
        AssertMoney(fullItem.GetProperty("unappliedAmount"), 0m, "PLN");

        JsonElement reversedItem = Find(root, reversed.Id.Value);
        AssertMoney(reversedItem.GetProperty("allocatedAmount"), 0m, "PLN");
        AssertMoney(reversedItem.GetProperty("unappliedAmount"), 80m, "PLN");

        JsonElement sourcedItem = Find(root, sourced.Id.Value);
        Assert.Equal("BANK_CSV", sourcedItem.GetProperty("sourceSystem").GetString());
        Assert.Equal("TX-123", sourcedItem.GetProperty("externalId").GetString());

        using HttpClient otherClient = Client(organizationB);
        using JsonDocument otherDocument = await ReadOkAsync(await otherClient.GetAsync("/api/bank-transactions"));
        JsonElement otherItems = otherDocument.RootElement.GetProperty("items");
        Assert.Equal(1, otherDocument.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(otherTenant.Id.Value, Assert.Single(otherItems.EnumerateArray()).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Organization_query_string_and_header_cannot_switch_tenant()
    {
        OrganizationId organizationA = OrganizationId.New();
        OrganizationId organizationB = OrganizationId.New();
        BankTransaction own = Create(organizationA, 20m, new DateOnly(2026, 5, 1), "PAY-A");
        BankTransaction foreign = Create(organizationB, 30m, new DateOnly(2026, 5, 2), "PAY-B");
        await SaveAsync(own, foreign);
        using HttpClient client = Client(organizationA);

        HttpResponseMessage queryResponse = await client.GetAsync(
            $"/api/bank-transactions?organizationId={organizationB.Value}&sort=bookingDate");
        using JsonDocument queryDocument = await ReadOkAsync(queryResponse);
        Assert.Contains(
            queryDocument.RootElement.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("reference").GetString() == "PAY-A");
        Assert.DoesNotContain(
            queryDocument.RootElement.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("reference").GetString() == "PAY-B");

        client.DefaultRequestHeaders.Add("X-Organization-Id", organizationB.Value.ToString());
        using JsonDocument headerDocument = await ReadOkAsync(await client.GetAsync("/api/bank-transactions"));
        Assert.DoesNotContain(
            headerDocument.RootElement.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("reference").GetString() == "PAY-B");
    }

    [Fact]
    public async Task Pages_follow_bank_transaction_id_order_and_keep_total_count()
    {
        OrganizationId organizationId = OrganizationId.New();
        BankTransaction first = Create(organizationId, 1m, new DateOnly(2026, 6, 1), "ONE");
        BankTransaction second = Create(organizationId, 2m, new DateOnly(2026, 6, 2), "TWO");
        BankTransaction third = Create(organizationId, 3m, new DateOnly(2026, 6, 3), "THREE");
        await SaveAsync(first, second, third);
        List<Guid> expected = await LoadOrderAsync(organizationId);
        using HttpClient client = Client(organizationId);

        List<Guid> actual = [];
        for (int page = 1; page <= 3; page++)
        {
            using JsonDocument document = await ReadOkAsync(
                await client.GetAsync($"/api/bank-transactions?page={page}&pageSize=1"));
            JsonElement root = document.RootElement;
            Assert.Equal(page, root.GetProperty("page").GetInt32());
            Assert.Equal(1, root.GetProperty("pageSize").GetInt32());
            Assert.Equal(3, root.GetProperty("totalCount").GetInt32());
            actual.Add(Assert.Single(root.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        }

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=101")]
    [InlineData("page=2147483647&pageSize=100")]
    public async Task Invalid_pagination_returns_400_problem_details(string query)
    {
        using HttpClient client = Client(OrganizationId.New());

        HttpResponseMessage response = await client.GetAsync($"/api/bank-transactions?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(400, document.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(
            ReconciliationApplicationErrors.InvalidBankTransactionPagination.Code,
            document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Missing_or_unusable_credentials_are_rejected()
    {
        using HttpClient anonymous = _factory.CreateApiClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/bank-transactions")).StatusCode);

        using HttpClient invalid = _factory.CreateApiClient(TestAccessTokens.CreateSignedWithOtherKey(Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Unauthorized, (await invalid.GetAsync("/api/bank-transactions")).StatusCode);

        using HttpClient missingOrganization = _factory.CreateApiClient(TestAccessTokens.CreateWithoutOrganizationClaim());
        Assert.Equal(HttpStatusCode.Forbidden, (await missingOrganization.GetAsync("/api/bank-transactions")).StatusCode);

        using HttpClient emptyOrganization = _factory.CreateApiClient(
            TestAccessTokens.CreateWithOrganizationClaim("00000000-0000-0000-0000-000000000000"));
        Assert.Equal(HttpStatusCode.Forbidden, (await emptyOrganization.GetAsync("/api/bank-transactions")).StatusCode);
    }

    [Fact]
    public async Task A_valid_page_stays_available_when_another_page_is_inconsistent()
    {
        OrganizationId organizationId = OrganizationId.New();
        BankTransaction earlier = Create(organizationId, 12m, new DateOnly(2026, 7, 1), "VISIBLE");
        BankTransaction later = Create(organizationId, 77.77m, new DateOnly(2026, 7, 2), "SECRET-PAY");
        await SaveAsync(earlier, later);
        List<Guid> order = await LoadOrderAsync(organizationId);
        Guid corruptedId = order[1];
        Guid visibleId = order[0];
        await using ReconciliationDbContext context = _fixture.CreateContext();
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE [reconciliation].[BankTransactions]
            SET [AllocatedCurrency] = 'USD',
                [SourceSystem] = 'SECRET-SYS',
                [ExternalId] = 'SECRET-EXT'
            WHERE [Id] = {corruptedId}
            """);
        using HttpClient client = Client(organizationId);

        using JsonDocument healthy = await ReadOkAsync(
            await client.GetAsync("/api/bank-transactions?page=1&pageSize=1"));
        Assert.Equal(2, healthy.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(visibleId, Assert.Single(healthy.RootElement.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());

        HttpResponseMessage broken = await client.GetAsync("/api/bank-transactions?page=2&pageSize=1");
        string body = await broken.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.InternalServerError, broken.StatusCode);
        using JsonDocument problem = JsonDocument.Parse(body);
        Assert.Equal(
            ReconciliationApplicationErrors.BankTransactionListInconsistentMoney.Code,
            problem.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("items", body, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-PAY", body, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-SYS", body, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-EXT", body, StringComparison.Ordinal);
        Assert.DoesNotContain("77.77", body, StringComparison.Ordinal);
        Assert.DoesNotContain("USD", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("negative-amount")]
    [InlineData("negative-allocated")]
    [InlineData("allocated-exceeds-principal")]
    [InlineData("invalid-currency")]
    public async Task Inconsistent_persisted_money_returns_500_without_leaking_values(string defect)
    {
        OrganizationId organizationId = OrganizationId.New();
        BankTransaction bankTransaction = Create(organizationId, 50m, new DateOnly(2026, 8, 1), "SECRET-ROW");
        await SaveAsync(bankTransaction);
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Guid id = bankTransaction.Id.Value;
        if (defect == "negative-amount")
        {
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE [reconciliation].[BankTransactions] SET [Amount] = {-1m} WHERE [Id] = {id}");
        }
        else if (defect == "negative-allocated")
        {
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE [reconciliation].[BankTransactions] SET [AllocatedAmount] = {-1m} WHERE [Id] = {id}");
        }
        else if (defect == "allocated-exceeds-principal")
        {
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE [reconciliation].[BankTransactions] SET [AllocatedAmount] = {1000m} WHERE [Id] = {id}");
        }
        else
        {
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE [reconciliation].[BankTransactions] SET [Currency] = {"12X"} WHERE [Id] = {id}");
        }
        using HttpClient client = Client(organizationId);

        HttpResponseMessage response = await client.GetAsync("/api/bank-transactions");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("SECRET-ROW", body, StringComparison.Ordinal);
        using JsonDocument document = JsonDocument.Parse(body);
        Assert.Equal(
            ReconciliationApplicationErrors.BankTransactionListInconsistentMoney.Code,
            document.RootElement.GetProperty("code").GetString());
        Assert.False(document.RootElement.TryGetProperty("items", out _));
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    private HttpClient Client(OrganizationId organizationId)
    {
        return _factory.CreateApiClient(TestAccessTokens.Create(organizationId.Value));
    }

    private async Task SaveAsync(params BankTransaction[] bankTransactions)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        context.BankTransactions.AddRange(bankTransactions);
        await context.SaveChangesAsync();
    }

    private async Task<List<Guid>> LoadOrderAsync(OrganizationId organizationId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        return await context.BankTransactions
            .AsNoTracking()
            .Where(bankTransaction => bankTransaction.OrganizationId == organizationId)
            .OrderBy(bankTransaction => bankTransaction.Id)
            .Select(bankTransaction => bankTransaction.Id.Value)
            .ToListAsync();
    }

    private static BankTransaction Create(
        OrganizationId organizationId,
        decimal amount,
        DateOnly bookingDate,
        string? reference)
    {
        return new BankTransaction(
            BankTransactionId.New(),
            organizationId,
            new Money(amount, PersistenceTestData.Pln),
            bookingDate,
            reference);
    }

    private static JsonElement Find(JsonElement root, Guid id)
    {
        return Assert.Single(
            root.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("id").GetGuid() == id);
    }

    private static void AssertMoney(JsonElement money, decimal amount, string currency)
    {
        Assert.Equal(JsonValueKind.Number, money.GetProperty("amount").ValueKind);
        Assert.Equal(amount, money.GetProperty("amount").GetDecimal());
        Assert.Equal(currency, money.GetProperty("currency").GetString());
    }

    private static async Task<JsonDocument> ReadOkAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }
}