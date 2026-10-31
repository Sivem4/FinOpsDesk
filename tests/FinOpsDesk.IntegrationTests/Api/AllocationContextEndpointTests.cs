using System.Net;
using System.Text;
using System.Text.Json;

using FinOpsDesk.IntegrationTests.Reconciliation.Persistence;
using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Imports;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;

namespace FinOpsDesk.IntegrationTests.Api;

[Collection(SqlServerCollection.Name)]
public sealed class AllocationContextEndpointTests : IDisposable
{
    private static readonly CurrencyCode Eur = new("EUR");

    private readonly SqlServerFixture _fixture;
    private readonly FinOpsDeskApiFactory _factory;

    public AllocationContextEndpointTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
        _factory = new FinOpsDeskApiFactory(fixture.ConnectionString);
    }

    [Fact]
    public async Task Context_returns_partial_balances_source_identity_and_booking_date()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = ImportPersistenceData.ImportedInvoice(
            organizationId,
            ImportPersistenceData.Identity("erp", "ext-100"),
            "INV-100",
            200.2501m);
        Assert.True(invoice.Allocate(new Money(50.0001m, PersistenceTestData.Pln)).IsSuccess);
        BankTransaction bankTransaction = ImportPersistenceData.ImportedBankTransaction(
            organizationId,
            ImportPersistenceData.Identity("bank", "bank-100"),
            180.2501m,
            "PAY-OTHER");
        Assert.True(bankTransaction.Allocate(new Money(30.0001m, PersistenceTestData.Pln)).IsSuccess);
        await SeedAsync(invoice, bankTransaction);
        using HttpClient client = Client(organizationId);
        client.DefaultRequestHeaders.Add("X-Organization-Id", Guid.NewGuid().ToString());

        HttpResponseMessage response = await client.GetAsync(ContextPath(invoice.Id.Value, bankTransaction.Id.Value));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;
        JsonElement invoiceBody = root.GetProperty("invoice");
        JsonElement bankBody = root.GetProperty("bankTransaction");

        Assert.Equal(invoice.Id.Value, invoiceBody.GetProperty("invoiceId").GetGuid());
        Assert.Equal("INV-100", invoiceBody.GetProperty("invoiceNumber").GetString());
        AssertMoney(invoiceBody.GetProperty("totalAmount"), 200.2501m, "PLN");
        AssertMoney(invoiceBody.GetProperty("allocatedAmount"), 50.0001m, "PLN");
        AssertMoney(invoiceBody.GetProperty("outstandingAmount"), 150.2500m, "PLN");
        Assert.Equal("ERP", invoiceBody.GetProperty("sourceSystem").GetString());
        Assert.Equal("ext-100", invoiceBody.GetProperty("externalId").GetString());
        Assert.Equal(bankTransaction.Id.Value, bankBody.GetProperty("bankTransactionId").GetGuid());
        Assert.Equal("PAY-OTHER", bankBody.GetProperty("reference").GetString());
        Assert.Equal("2026-08-24", bankBody.GetProperty("bookingDate").GetString());
        AssertMoney(bankBody.GetProperty("amount"), 180.2501m, "PLN");
        AssertMoney(bankBody.GetProperty("allocatedAmount"), 30.0001m, "PLN");
        AssertMoney(bankBody.GetProperty("unappliedAmount"), 150.2500m, "PLN");
        Assert.Equal("BANK", bankBody.GetProperty("sourceSystem").GetString());
        Assert.Equal("bank-100", bankBody.GetProperty("externalId").GetString());
        Assert.False(root.TryGetProperty("organizationId", out _));
        Assert.False(root.TryGetProperty("rowVersion", out _));
        Assert.False(invoiceBody.TryGetProperty("organizationId", out _));
        Assert.False(bankBody.TryGetProperty("rowVersion", out _));
        Assert.False(root.TryGetProperty("canAllocate", out _));
        Assert.DoesNotContain("suggestedAmount", body, StringComparison.Ordinal);
        Assert.Contains("200.2501", body, StringComparison.Ordinal);
        Assert.Contains("0.0001", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Context_returns_null_source_identity_and_null_reference()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 90m, "INV-BARE");
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(
            organizationId,
            90m,
            new DateOnly(2026, 10, 30),
            reference: null);
        await SeedAsync(invoice, bankTransaction);
        using HttpClient client = Client(organizationId);

        using JsonDocument document = await ReadOkAsync(client, invoice.Id.Value, bankTransaction.Id.Value);

        JsonElement invoiceBody = document.RootElement.GetProperty("invoice");
        JsonElement bankBody = document.RootElement.GetProperty("bankTransaction");
        Assert.Equal(JsonValueKind.Null, invoiceBody.GetProperty("sourceSystem").ValueKind);
        Assert.Equal(JsonValueKind.Null, invoiceBody.GetProperty("externalId").ValueKind);
        Assert.Equal(JsonValueKind.Null, bankBody.GetProperty("sourceSystem").ValueKind);
        Assert.Equal(JsonValueKind.Null, bankBody.GetProperty("externalId").ValueKind);
        Assert.Equal(JsonValueKind.Null, bankBody.GetProperty("reference").ValueKind);
        Assert.Equal("2026-10-30", bankBody.GetProperty("bookingDate").GetString());
    }

    [Fact]
    public async Task Context_returns_zero_remaining_balances()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 40m, "INV-ZERO");
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(
            organizationId,
            40m,
            reference: "PAY-ZERO");
        Assert.True(invoice.Allocate(new Money(40m, PersistenceTestData.Pln)).IsSuccess);
        Assert.True(bankTransaction.Allocate(new Money(40m, PersistenceTestData.Pln)).IsSuccess);
        await SeedAsync(invoice, bankTransaction);
        using HttpClient client = Client(organizationId);

        using JsonDocument document = await ReadOkAsync(client, invoice.Id.Value, bankTransaction.Id.Value);

        AssertMoney(document.RootElement.GetProperty("invoice").GetProperty("outstandingAmount"), 0m, "PLN");
        AssertMoney(document.RootElement.GetProperty("bankTransaction").GetProperty("unappliedAmount"), 0m, "PLN");
    }

    [Fact]
    public async Task Different_currencies_return_200_without_conversion()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 100m, "INV-PLN");
        BankTransaction bankTransaction = new(
            BankTransactionId.New(),
            organizationId,
            new Money(80m, Eur),
            new DateOnly(2026, 10, 30),
            "PAY-EUR");
        await SeedAsync(invoice, bankTransaction);
        using HttpClient client = Client(organizationId);

        using JsonDocument document = await ReadOkAsync(client, invoice.Id.Value, bankTransaction.Id.Value);

        AssertMoney(document.RootElement.GetProperty("invoice").GetProperty("outstandingAmount"), 100m, "PLN");
        AssertMoney(document.RootElement.GetProperty("bankTransaction").GetProperty("unappliedAmount"), 80m, "EUR");
        Assert.False(document.RootElement.TryGetProperty("convertedAmount", out _));
    }

    [Fact]
    public async Task Missing_invoice_returns_404()
    {
        OrganizationId organizationId = OrganizationId.New();
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(
            organizationId,
            15m,
            reference: "ONLY-BANK");
        await SeedAsync(bankTransaction);
        using HttpClient client = Client(organizationId);

        await AssertProblemAsync(
            await client.GetAsync(ContextPath(Guid.NewGuid(), bankTransaction.Id.Value)),
            HttpStatusCode.NotFound,
            ReconciliationApplicationErrors.InvoiceNotFound.Code,
            "ONLY-BANK");
    }

    [Fact]
    public async Task Missing_bank_transaction_returns_404()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 15m, "ONLY-INV");
        await SeedAsync(invoice);
        using HttpClient client = Client(organizationId);

        await AssertProblemAsync(
            await client.GetAsync(ContextPath(invoice.Id.Value, Guid.NewGuid())),
            HttpStatusCode.NotFound,
            ReconciliationApplicationErrors.BankTransactionNotFound.Code,
            "ONLY-INV");
    }

    [Fact]
    public async Task Foreign_invoice_returns_404_without_its_details()
    {
        OrganizationId owner = OrganizationId.New();
        OrganizationId caller = OrganizationId.New();
        Invoice foreignInvoice = PersistenceTestData.CreateInvoice(owner, 1234.56m, "FOREIGN-INV-8841");
        BankTransaction localBank = PersistenceTestData.CreateBankTransaction(
            caller,
            20m,
            reference: "LOCAL-PAY");
        await SeedAsync(foreignInvoice, localBank);
        using HttpClient client = Client(caller);

        string body = await AssertProblemAsync(
            await client.GetAsync(ContextPath(foreignInvoice.Id.Value, localBank.Id.Value)),
            HttpStatusCode.NotFound,
            ReconciliationApplicationErrors.InvoiceNotFound.Code,
            "FOREIGN-INV-8841");
        Assert.DoesNotContain("1234.56", body, StringComparison.Ordinal);
        Assert.DoesNotContain(owner.Value.ToString(), body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Foreign_bank_transaction_returns_404_without_its_details()
    {
        OrganizationId owner = OrganizationId.New();
        OrganizationId caller = OrganizationId.New();
        Invoice localInvoice = PersistenceTestData.CreateInvoice(caller, 20m, "LOCAL-INV");
        BankTransaction foreignBank = PersistenceTestData.CreateBankTransaction(
            owner,
            4321.09m,
            reference: "FOREIGN-PAY-5520");
        await SeedAsync(localInvoice, foreignBank);
        using HttpClient client = Client(caller);

        string body = await AssertProblemAsync(
            await client.GetAsync(ContextPath(localInvoice.Id.Value, foreignBank.Id.Value)),
            HttpStatusCode.NotFound,
            ReconciliationApplicationErrors.BankTransactionNotFound.Code,
            "FOREIGN-PAY-5520");
        Assert.DoesNotContain("4321.09", body, StringComparison.Ordinal);
        Assert.DoesNotContain(owner.Value.ToString(), body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("not-a-guid", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")]
    [InlineData("00000000-0000-0000-0000-000000000000", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")]
    [InlineData("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "not-a-guid")]
    [InlineData("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "00000000-0000-0000-0000-000000000000")]
    public async Task Invalid_or_empty_guid_returns_400(string invoiceId, string bankTransactionId)
    {
        using HttpClient client = Client(OrganizationId.New());

        await AssertProblemAsync(
            await client.GetAsync($"/api/allocations/context?invoiceId={invoiceId}&bankTransactionId={bankTransactionId}"),
            HttpStatusCode.BadRequest,
            ReconciliationApplicationErrors.AllocationContextInvalidRequest.Code);
    }

    [Fact]
    public async Task Missing_duplicate_and_unknown_query_parameters_return_400()
    {
        Guid invoiceId = Guid.NewGuid();
        Guid bankTransactionId = Guid.NewGuid();
        using HttpClient client = Client(OrganizationId.New());
        string[] paths =
        [
            "/api/allocations/context",
            $"/api/allocations/context?invoiceId={invoiceId}",
            $"/api/allocations/context?bankTransactionId={bankTransactionId}",
            $"/api/allocations/context?invoiceId={invoiceId}&invoiceId={Guid.NewGuid()}&bankTransactionId={bankTransactionId}",
            $"/api/allocations/context?invoiceId={invoiceId}&InvoiceId={Guid.NewGuid()}&bankTransactionId={bankTransactionId}",
            $"/api/allocations/context?invoiceId={invoiceId}&bankTransactionId={bankTransactionId}&bankTransactionId={Guid.NewGuid()}",
            $"/api/allocations/context?invoiceId={invoiceId}&bankTransactionId={bankTransactionId}&extra=1",
            $"/api/allocations/context?invoiceId={invoiceId}&bankTransactionId={bankTransactionId}&organizationId={Guid.NewGuid()}",
        ];

        foreach (string path in paths)
        {
            await AssertProblemAsync(
                await client.GetAsync(path),
                HttpStatusCode.BadRequest,
                ReconciliationApplicationErrors.AllocationContextInvalidRequest.Code,
                path);
        }
    }

    [Fact]
    public async Task Missing_token_and_invalid_token_return_401()
    {
        string path = ContextPath(Guid.NewGuid(), Guid.NewGuid());
        using HttpClient anonymous = _factory.CreateApiClient();
        using HttpClient invalid = _factory.CreateApiClient(
            TestAccessTokens.CreateSignedWithOtherKey(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await invalid.GetAsync(path)).StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Unusable_organization_claim_returns_403(string? organizationClaim)
    {
        string token = organizationClaim is null
            ? TestAccessTokens.CreateWithoutOrganizationClaim()
            : TestAccessTokens.CreateWithOrganizationClaim(organizationClaim);
        using HttpClient client = _factory.CreateApiClient(token);

        HttpResponseMessage response = await client.GetAsync(ContextPath(Guid.NewGuid(), Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Read_without_subject_returns_200()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 12m, "INV-NOSUB");
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(organizationId, 12m);
        await SeedAsync(invoice, bankTransaction);
        using HttpClient client = _factory.CreateApiClient(
            TestAccessTokens.CreateForActor(organizationId.Value, subject: null));

        using JsonDocument document = await ReadOkAsync(client, invoice.Id.Value, bankTransaction.Id.Value);

        Assert.Equal("INV-NOSUB", document.RootElement.GetProperty("invoice").GetProperty("invoiceNumber").GetString());
    }

    [Fact]
    public async Task Get_does_not_create_an_allocation_or_audit_row()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 25m, "INV-READ");
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(
            organizationId,
            25m,
            reference: "PAY-READ");
        await SeedAsync(invoice, bankTransaction);
        using HttpClient client = Client(organizationId);

        using JsonDocument document = await ReadOkAsync(client, invoice.Id.Value, bankTransaction.Id.Value);

        AssertMoney(document.RootElement.GetProperty("invoice").GetProperty("outstandingAmount"), 25m, "PLN");
        await using ReconciliationDbContext reload = _fixture.CreateContext();
        Assert.Equal(0, await reload.Allocations.CountAsync(allocation => allocation.OrganizationId == organizationId));
        Assert.Equal(
            0,
            await reload.FinancialAuditEntries.CountAsync(entry => entry.OrganizationId == organizationId));
        Invoice storedInvoice = await reload.Invoices.SingleAsync(candidate => candidate.Id == invoice.Id);
        BankTransaction storedBank = await reload.BankTransactions.SingleAsync(
            candidate => candidate.Id == bankTransaction.Id);
        Assert.Equal(Money.Zero(PersistenceTestData.Pln), storedInvoice.AllocatedAmount);
        Assert.Equal(Money.Zero(PersistenceTestData.Pln), storedBank.AllocatedAmount);
    }

    [Fact]
    public async Task Later_post_uses_the_balance_after_the_context_read()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 200m, "INV-RACE");
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(
            organizationId,
            200m,
            reference: "PAY-RACE");
        await SeedAsync(invoice, bankTransaction);
        using HttpClient client = Client(organizationId);

        using (JsonDocument before = await ReadOkAsync(client, invoice.Id.Value, bankTransaction.Id.Value))
        {
            AssertMoney(before.RootElement.GetProperty("invoice").GetProperty("outstandingAmount"), 200m, "PLN");
            AssertMoney(before.RootElement.GetProperty("bankTransaction").GetProperty("unappliedAmount"), 200m, "PLN");
        }

        HttpResponseMessage first = await client.SendAsync(
            AllocateRequest(invoice.Id.Value, bankTransaction.Id.Value, 50m, "PLN"));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        using (JsonDocument middle = await ReadOkAsync(client, invoice.Id.Value, bankTransaction.Id.Value))
        {
            AssertMoney(middle.RootElement.GetProperty("invoice").GetProperty("outstandingAmount"), 150m, "PLN");
            AssertMoney(middle.RootElement.GetProperty("bankTransaction").GetProperty("unappliedAmount"), 150m, "PLN");
        }

        await AssertProblemAsync(
            await client.SendAsync(AllocateRequest(invoice.Id.Value, bankTransaction.Id.Value, 200m, "PLN")),
            HttpStatusCode.Conflict,
            ReconciliationErrors.InvoiceAllocationExceedsOutstanding.Code);

        HttpResponseMessage remainder = await client.SendAsync(
            AllocateRequest(invoice.Id.Value, bankTransaction.Id.Value, 150m, "PLN"));
        Assert.Equal(HttpStatusCode.Created, remainder.StatusCode);

        using JsonDocument after = await ReadOkAsync(client, invoice.Id.Value, bankTransaction.Id.Value);
        AssertMoney(after.RootElement.GetProperty("invoice").GetProperty("outstandingAmount"), 0m, "PLN");
        AssertMoney(after.RootElement.GetProperty("bankTransaction").GetProperty("unappliedAmount"), 0m, "PLN");
    }

    [Fact]
    public async Task Inconsistent_stored_currency_returns_500_without_the_record_details()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 77.77m, "SECRET-INV");
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(
            organizationId,
            77.77m,
            reference: "SECRET-PAY");
        await SeedAsync(invoice, bankTransaction);
        await using ReconciliationDbContext context = _fixture.CreateContext();
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE [reconciliation].[Invoices]
            SET [AllocatedCurrency] = 'USD'
            WHERE [Id] = {invoice.Id.Value}
            """);
        using HttpClient client = Client(organizationId);

        string body = await AssertProblemAsync(
            await client.GetAsync(ContextPath(invoice.Id.Value, bankTransaction.Id.Value)),
            HttpStatusCode.InternalServerError,
            ReconciliationApplicationErrors.AllocationContextInconsistentMoney.Code,
            "SECRET-INV");
        Assert.DoesNotContain("SECRET-PAY", body, StringComparison.Ordinal);
        Assert.DoesNotContain("77.77", body, StringComparison.Ordinal);
        await using ReconciliationDbContext reload = _fixture.CreateContext();
        Assert.Equal(0, await reload.Allocations.CountAsync(allocation => allocation.OrganizationId == organizationId));
    }

    [Fact]
    public async Task Context_returns_500_when_bank_transaction_currency_is_inconsistent()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 77.77m, "SECRET-BANK-INV");
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(
            organizationId,
            77.77m,
            reference: "SECRET-BANK-PAY");
        await SeedAsync(invoice, bankTransaction);
        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE [reconciliation].[BankTransactions]
                SET [AllocatedCurrency] = 'USD'
                WHERE [Id] = {bankTransaction.Id.Value}
                """);
        }

        using HttpClient client = Client(organizationId);
        string body = await AssertProblemAsync(
            await client.GetAsync(ContextPath(invoice.Id.Value, bankTransaction.Id.Value)),
            HttpStatusCode.InternalServerError,
            ReconciliationApplicationErrors.AllocationContextInconsistentMoney.Code,
            "SECRET-BANK-INV");
        Assert.DoesNotContain("SECRET-BANK-PAY", body, StringComparison.Ordinal);
        Assert.DoesNotContain("77.77", body, StringComparison.Ordinal);
        AssertNoContextDto(body);
        await AssertStoredPairUnchangedAsync(
            organizationId,
            invoice,
            bankTransaction,
            invoiceAllocated: 0m,
            invoiceAllocatedCurrency: "PLN",
            bankAllocated: 0m,
            bankAllocatedCurrency: "USD");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Context_rejects_allocated_amount_greater_than_principal(bool corruptInvoice)
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 100m, "SECRET-OVER-INV");
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(
            organizationId,
            100m,
            reference: "SECRET-OVER-PAY");
        await SeedAsync(invoice, bankTransaction);
        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            if (corruptInvoice)
            {
                await context.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    UPDATE [reconciliation].[Invoices]
                    SET [AllocatedAmount] = {120m}
                    WHERE [Id] = {invoice.Id.Value}
                    """);
            }
            else
            {
                await context.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    UPDATE [reconciliation].[BankTransactions]
                    SET [AllocatedAmount] = {120m}
                    WHERE [Id] = {bankTransaction.Id.Value}
                    """);
            }
        }

        using HttpClient client = Client(organizationId);
        string body = await AssertProblemAsync(
            await client.GetAsync(ContextPath(invoice.Id.Value, bankTransaction.Id.Value)),
            HttpStatusCode.InternalServerError,
            ReconciliationApplicationErrors.AllocationContextInconsistentMoney.Code,
            "SECRET-OVER-INV");
        Assert.DoesNotContain("SECRET-OVER-PAY", body, StringComparison.Ordinal);
        AssertDoesNotContainOutsideTrace(body, "120");
        AssertDoesNotContainOutsideTrace(body, "100");
        AssertNoContextDto(body);
        await AssertStoredPairUnchangedAsync(
            organizationId,
            invoice,
            bankTransaction,
            invoiceAllocated: corruptInvoice ? 120m : 0m,
            invoiceAllocatedCurrency: "PLN",
            bankAllocated: corruptInvoice ? 0m : 120m,
            bankAllocatedCurrency: "PLN");
    }

    public void Dispose()
    {
        _factory.Dispose();
    }

    private HttpClient Client(OrganizationId organizationId)
    {
        return _factory.CreateApiClient(TestAccessTokens.Create(organizationId.Value));
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

    private static void AssertDoesNotContainOutsideTrace(string body, string value)
    {
        using JsonDocument document = JsonDocument.Parse(body);
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (property.NameEquals("traceId"))
            {
                continue;
            }

            Assert.DoesNotContain(value, property.Value.GetRawText(), StringComparison.Ordinal);
        }
    }

    private static async Task<JsonDocument> ReadOkAsync(HttpClient client, Guid invoiceId, Guid bankTransactionId)
    {
        HttpResponseMessage response = await client.GetAsync(ContextPath(invoiceId, bankTransactionId));
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body);
    }

    private static async Task<string> AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode statusCode,
        string code,
        string? absentText = null)
    {
        string body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == statusCode,
            $"Expected {(int)statusCode} but got {(int)response.StatusCode}. Body: {body}");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using JsonDocument document = JsonDocument.Parse(body);
        Assert.Equal((int)statusCode, document.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("detail").GetString()));
        if (absentText is not null)
        {
            Assert.DoesNotContain(absentText, body, StringComparison.Ordinal);
        }

        return body;
    }

    private static void AssertNoContextDto(string body)
    {
        using JsonDocument document = JsonDocument.Parse(body);
        Assert.False(document.RootElement.TryGetProperty("invoice", out _));
        Assert.False(document.RootElement.TryGetProperty("bankTransaction", out _));
        Assert.DoesNotContain("outstandingAmount", body, StringComparison.Ordinal);
        Assert.DoesNotContain("unappliedAmount", body, StringComparison.Ordinal);
        Assert.DoesNotContain("invoiceNumber", body, StringComparison.Ordinal);
    }

    private async Task AssertStoredPairUnchangedAsync(
        OrganizationId organizationId,
        Invoice invoice,
        BankTransaction bankTransaction,
        decimal invoiceAllocated,
        string invoiceAllocatedCurrency,
        decimal bankAllocated,
        string bankAllocatedCurrency)
    {
        await using ReconciliationDbContext reload = _fixture.CreateContext();
        Assert.Equal(0, await reload.Allocations.CountAsync(allocation => allocation.OrganizationId == organizationId));
        Assert.Equal(
            0,
            await reload.FinancialAuditEntries.CountAsync(entry => entry.OrganizationId == organizationId));
        Invoice storedInvoice = await reload.Invoices.SingleAsync(candidate => candidate.Id == invoice.Id);
        BankTransaction storedBank = await reload.BankTransactions.SingleAsync(
            candidate => candidate.Id == bankTransaction.Id);
        Assert.Equal(invoice.TotalAmount.Amount, storedInvoice.TotalAmount.Amount);
        Assert.Equal(invoice.TotalAmount.Currency.Value, storedInvoice.TotalAmount.Currency.Value);
        Assert.Equal(invoiceAllocated, storedInvoice.AllocatedAmount.Amount);
        Assert.Equal(invoiceAllocatedCurrency, storedInvoice.AllocatedAmount.Currency.Value);
        Assert.Equal(bankTransaction.Amount.Amount, storedBank.Amount.Amount);
        Assert.Equal(bankTransaction.Amount.Currency.Value, storedBank.Amount.Currency.Value);
        Assert.Equal(bankAllocated, storedBank.AllocatedAmount.Amount);
        Assert.Equal(bankAllocatedCurrency, storedBank.AllocatedAmount.Currency.Value);
    }

    private static void AssertMoney(JsonElement money, decimal amount, string currency)
    {
        JsonElement value = money.GetProperty("amount");
        Assert.Equal(JsonValueKind.Number, value.ValueKind);
        Assert.Equal(amount, value.GetDecimal());
        Assert.Equal(currency, money.GetProperty("currency").GetString());
    }

    private static string ContextPath(Guid invoiceId, Guid bankTransactionId)
    {
        return $"/api/allocations/context?invoiceId={invoiceId:D}&bankTransactionId={bankTransactionId:D}";
    }

    private static HttpRequestMessage AllocateRequest(
        Guid invoiceId,
        Guid bankTransactionId,
        decimal amount,
        string currency)
    {
        string amountText = amount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string json =
            "{\"invoiceId\":\"" + invoiceId.ToString("D")
            + "\",\"bankTransactionId\":\"" + bankTransactionId.ToString("D")
            + "\",\"amount\":{\"amount\":" + amountText
            + ",\"currency\":\"" + currency + "\"}}";
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/allocations")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString("D"));
        return request;
    }
}