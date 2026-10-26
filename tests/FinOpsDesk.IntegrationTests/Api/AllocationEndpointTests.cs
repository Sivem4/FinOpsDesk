using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

using FinOpsDesk.IntegrationTests.Reconciliation.Persistence;
using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FinOpsDesk.IntegrationTests.Api;

[Collection(SqlServerCollection.Name)]
public sealed class AllocationEndpointTests : IDisposable
{
    private readonly SqlServerFixture _fixture;
    private readonly FinOpsDeskApiFactory _factory;

    public AllocationEndpointTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
        _factory = new FinOpsDeskApiFactory(fixture.ConnectionString);
    }

    [Fact]
    public async Task Partial_allocation_returns_201_and_replay_returns_200()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 1000m, "INV-PART");
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(
            organizationId,
            1000m,
            reference: "INV-PART");
        await SeedAsync(invoice, bankTransaction);
        string idempotencyKey = Guid.NewGuid().ToString("D");
        using HttpClient client = _factory.CreateApiClient(TestAccessTokens.Create(organizationId.Value));

        HttpResponseMessage created = await client.SendAsync(
            AllocateRequest(invoice.Id.Value, bankTransaction.Id.Value, 400m, "PLN", idempotencyKey));
        HttpResponseMessage replayed = await client.SendAsync(
            AllocateRequest(invoice.Id.Value, bankTransaction.Id.Value, 400m, "PLN", idempotencyKey));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replayed.StatusCode);
        using JsonDocument createdDocument = await ReadJsonAsync(created);
        using JsonDocument replayedDocument = await ReadJsonAsync(replayed);
        JsonElement createdBody = createdDocument.RootElement;
        JsonElement replayedBody = replayedDocument.RootElement;

        Assert.False(createdBody.GetProperty("replayed").GetBoolean());
        Assert.True(replayedBody.GetProperty("replayed").GetBoolean());
        Assert.Equal(
            createdBody.GetProperty("allocationId").GetGuid(),
            replayedBody.GetProperty("allocationId").GetGuid());
        Assert.Equal(invoice.Id.Value, createdBody.GetProperty("invoiceId").GetGuid());
        Assert.Equal(bankTransaction.Id.Value, createdBody.GetProperty("bankTransactionId").GetGuid());
        Assert.Equal(400m, createdBody.GetProperty("amount").GetProperty("amount").GetDecimal());
        Assert.Equal("PLN", createdBody.GetProperty("amount").GetProperty("currency").GetString());
        Assert.Equal(400m, replayedBody.GetProperty("amount").GetProperty("amount").GetDecimal());
        Assert.False(createdBody.TryGetProperty("organizationId", out _));
        Assert.False(createdBody.TryGetProperty("rowVersion", out _));
        Assert.False(createdBody.TryGetProperty("idempotencyKey", out _));

        await using ReconciliationDbContext reload = _fixture.CreateContext();
        Invoice storedInvoice = await reload.Invoices.SingleAsync(candidate => candidate.Id == invoice.Id);
        BankTransaction storedBank = await reload.BankTransactions.SingleAsync(
            candidate => candidate.Id == bankTransaction.Id);
        Assert.Equal(new Money(400m, PersistenceTestData.Pln), storedInvoice.AllocatedAmount);
        Assert.Equal(new Money(600m, PersistenceTestData.Pln), storedInvoice.OutstandingAmount);
        Assert.Equal(new Money(400m, PersistenceTestData.Pln), storedBank.AllocatedAmount);
        Assert.Equal(new Money(600m, PersistenceTestData.Pln), storedBank.UnappliedAmount);
        Allocation stored = await reload.Allocations.SingleAsync(
            allocation => allocation.OrganizationId == organizationId);
        Assert.Equal(AllocationIdempotencyOperation.ExplicitAmount, stored.IdempotencyOperation);
        Assert.Equal(organizationId, stored.OrganizationId);
    }

    [Fact]
    public async Task Full_remaining_balance_can_be_allocated_when_references_differ()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 250.50m, "INV-FULL");
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(
            organizationId,
            250.50m,
            reference: "OTHER-REF");
        await SeedAsync(invoice, bankTransaction);
        string idempotencyKey = Guid.NewGuid().ToString("D");
        using HttpClient client = _factory.CreateApiClient(TestAccessTokens.Create(organizationId.Value));

        HttpResponseMessage created = await client.SendAsync(
            AllocateRequest(invoice.Id.Value, bankTransaction.Id.Value, 250.50m, "PLN", idempotencyKey));
        HttpResponseMessage replayed = await client.SendAsync(
            AllocateRequest(invoice.Id.Value, bankTransaction.Id.Value, 250.50m, "PLN", idempotencyKey));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replayed.StatusCode);
        using JsonDocument replayedDocument = await ReadJsonAsync(replayed);
        Assert.True(replayedDocument.RootElement.GetProperty("replayed").GetBoolean());
        Assert.Equal(250.50m, replayedDocument.RootElement.GetProperty("amount").GetProperty("amount").GetDecimal());

        await using ReconciliationDbContext reload = _fixture.CreateContext();
        Invoice storedInvoice = await reload.Invoices.SingleAsync(candidate => candidate.Id == invoice.Id);
        BankTransaction storedBank = await reload.BankTransactions.SingleAsync(
            candidate => candidate.Id == bankTransaction.Id);
        Assert.Equal(Money.Zero(PersistenceTestData.Pln), storedInvoice.OutstandingAmount);
        Assert.Equal(Money.Zero(PersistenceTestData.Pln), storedBank.UnappliedAmount);
    }

    [Fact]
    public async Task Mismatched_reference_still_allocates_the_explicit_amount()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 800m, "INV-A");
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(
            organizationId,
            800m,
            reference: "PAY-B");
        await SeedAsync(invoice, bankTransaction);
        using HttpClient client = _factory.CreateApiClient(TestAccessTokens.Create(organizationId.Value));

        HttpResponseMessage response = await client.SendAsync(
            AllocateRequest(
                invoice.Id.Value,
                bankTransaction.Id.Value,
                125m,
                "PLN",
                Guid.NewGuid().ToString("D")));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        await using ReconciliationDbContext reload = _fixture.CreateContext();
        Invoice storedInvoice = await reload.Invoices.SingleAsync(candidate => candidate.Id == invoice.Id);
        Assert.Equal(new Money(125m, PersistenceTestData.Pln), storedInvoice.AllocatedAmount);
    }

    [Fact]
    public async Task Missing_bank_reference_still_allocates_the_explicit_amount()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 90m, "INV-NOREF");
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(
            organizationId,
            90m,
            reference: null);
        await SeedAsync(invoice, bankTransaction);
        using HttpClient client = _factory.CreateApiClient(TestAccessTokens.Create(organizationId.Value));

        HttpResponseMessage response = await client.SendAsync(
            AllocateRequest(
                invoice.Id.Value,
                bankTransaction.Id.Value,
                40m,
                "PLN",
                Guid.NewGuid().ToString("D")));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        await using ReconciliationDbContext reload = _fixture.CreateContext();
        BankTransaction storedBank = await reload.BankTransactions.SingleAsync(
            candidate => candidate.Id == bankTransaction.Id);
        Assert.Null(storedBank.Reference);
        Assert.Equal(new Money(40m, PersistenceTestData.Pln), storedBank.AllocatedAmount);
    }

    [Fact]
    public async Task Overpayment_leaves_the_remainder_unapplied()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 400m, "INV-OVER");
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(
            organizationId,
            1000m,
            reference: "PAY-OVER");
        await SeedAsync(invoice, bankTransaction);
        using HttpClient client = _factory.CreateApiClient(TestAccessTokens.Create(organizationId.Value));

        HttpResponseMessage response = await client.SendAsync(
            AllocateRequest(
                invoice.Id.Value,
                bankTransaction.Id.Value,
                400m,
                "PLN",
                Guid.NewGuid().ToString("D")));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        await using ReconciliationDbContext reload = _fixture.CreateContext();
        Invoice storedInvoice = await reload.Invoices.SingleAsync(candidate => candidate.Id == invoice.Id);
        BankTransaction storedBank = await reload.BankTransactions.SingleAsync(
            candidate => candidate.Id == bankTransaction.Id);
        Assert.Equal(Money.Zero(PersistenceTestData.Pln), storedInvoice.OutstandingAmount);
        Assert.Equal(new Money(400m, PersistenceTestData.Pln), storedBank.AllocatedAmount);
        Assert.Equal(new Money(600m, PersistenceTestData.Pln), storedBank.UnappliedAmount);
    }

    [Fact]
    public async Task Same_pair_can_be_allocated_again_with_a_different_key()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 1000m);
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(organizationId, 1000m);
        await SeedAsync(invoice, bankTransaction);
        using HttpClient client = _factory.CreateApiClient(TestAccessTokens.Create(organizationId.Value));

        HttpResponseMessage first = await client.SendAsync(
            AllocateRequest(
                invoice.Id.Value,
                bankTransaction.Id.Value,
                400m,
                "PLN",
                Guid.NewGuid().ToString("D")));
        HttpResponseMessage second = await client.SendAsync(
            AllocateRequest(
                invoice.Id.Value,
                bankTransaction.Id.Value,
                250m,
                "PLN",
                Guid.NewGuid().ToString("D")));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        using JsonDocument firstDocument = await ReadJsonAsync(first);
        using JsonDocument secondDocument = await ReadJsonAsync(second);
        Assert.NotEqual(
            firstDocument.RootElement.GetProperty("allocationId").GetGuid(),
            secondDocument.RootElement.GetProperty("allocationId").GetGuid());

        await using ReconciliationDbContext reload = _fixture.CreateContext();
        List<Allocation> stored = await reload.Allocations
            .Where(allocation => allocation.OrganizationId == organizationId)
            .ToListAsync();
        Assert.Equal(2, stored.Count);
        Assert.Equal(650m, stored.Sum(allocation => allocation.Amount.Amount));
        Invoice storedInvoice = await reload.Invoices.SingleAsync(candidate => candidate.Id == invoice.Id);
        Assert.Equal(new Money(650m, PersistenceTestData.Pln), storedInvoice.AllocatedAmount);
    }

    [Fact]
    public async Task Trailing_zeros_within_decimal_scale_are_accepted()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 1000m);
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(organizationId, 1000m);
        await SeedAsync(invoice, bankTransaction);
        using HttpClient client = _factory.CreateApiClient(TestAccessTokens.Create(organizationId.Value));
        string json = $$"""
            {"invoiceId":"{{invoice.Id.Value}}","bankTransactionId":"{{bankTransaction.Id.Value}}","amount":{"amount":400.00000,"currency":"PLN"}
            """ + "}";

        HttpResponseMessage response = await client.SendAsync(
            AllocateRaw(json, Guid.NewGuid().ToString("D")));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using JsonDocument document = await ReadJsonAsync(response);
        Assert.Equal(400m, document.RootElement.GetProperty("amount").GetProperty("amount").GetDecimal());

        await using ReconciliationDbContext reload = _fixture.CreateContext();
        Allocation stored = await reload.Allocations.SingleAsync(
            allocation => allocation.OrganizationId == organizationId);
        Assert.Equal(new Money(400m, PersistenceTestData.Pln), stored.Amount);
    }

    [Fact]
    public async Task Same_key_with_a_different_amount_returns_409()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 1000m);
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(organizationId, 1000m);
        await SeedAsync(invoice, bankTransaction);
        string idempotencyKey = Guid.NewGuid().ToString("D");
        using HttpClient client = _factory.CreateApiClient(TestAccessTokens.Create(organizationId.Value));

        HttpResponseMessage created = await client.SendAsync(
            AllocateRequest(invoice.Id.Value, bankTransaction.Id.Value, 400m, "PLN", idempotencyKey));
        HttpResponseMessage conflict = await client.SendAsync(
            AllocateRequest(invoice.Id.Value, bankTransaction.Id.Value, 401m, "PLN", idempotencyKey));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        await AssertProblemAsync(
            conflict,
            HttpStatusCode.Conflict,
            ReconciliationApplicationErrors.IdempotencyPayloadConflict.Code);

        await using ReconciliationDbContext reload = _fixture.CreateContext();
        Allocation stored = await reload.Allocations.SingleAsync(
            allocation => allocation.OrganizationId == organizationId);
        Assert.Equal(new Money(400m, PersistenceTestData.Pln), stored.Amount);
    }

    [Fact]
    public async Task Same_key_with_a_different_currency_returns_409()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 1000m);
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(organizationId, 1000m);
        await SeedAsync(invoice, bankTransaction);
        string idempotencyKey = Guid.NewGuid().ToString("D");
        using HttpClient client = _factory.CreateApiClient(TestAccessTokens.Create(organizationId.Value));

        HttpResponseMessage created = await client.SendAsync(
            AllocateRequest(invoice.Id.Value, bankTransaction.Id.Value, 400m, "PLN", idempotencyKey));
        HttpResponseMessage conflict = await client.SendAsync(
            AllocateRequest(invoice.Id.Value, bankTransaction.Id.Value, 400m, "EUR", idempotencyKey));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        await AssertProblemAsync(
            conflict,
            HttpStatusCode.Conflict,
            ReconciliationApplicationErrors.IdempotencyPayloadConflict.Code);

        await using ReconciliationDbContext reload = _fixture.CreateContext();
        Assert.Equal(
            1,
            await reload.Allocations.CountAsync(allocation => allocation.OrganizationId == organizationId));
    }

    [Fact]
    public async Task Same_key_with_a_different_pair_returns_409()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice firstInvoice = PersistenceTestData.CreateInvoice(organizationId, 500m, "INV-1");
        Invoice secondInvoice = PersistenceTestData.CreateInvoice(organizationId, 500m, "INV-2");
        BankTransaction firstBank = PersistenceTestData.CreateBankTransaction(
            organizationId,
            500m,
            reference: "PAY-1");
        BankTransaction secondBank = PersistenceTestData.CreateBankTransaction(
            organizationId,
            500m,
            reference: "PAY-2");
        await SeedAsync(firstInvoice, secondInvoice, firstBank, secondBank);
        string idempotencyKey = Guid.NewGuid().ToString("D");
        using HttpClient client = _factory.CreateApiClient(TestAccessTokens.Create(organizationId.Value));

        HttpResponseMessage created = await client.SendAsync(
            AllocateRequest(firstInvoice.Id.Value, firstBank.Id.Value, 100m, "PLN", idempotencyKey));
        HttpResponseMessage conflict = await client.SendAsync(
            AllocateRequest(secondInvoice.Id.Value, secondBank.Id.Value, 100m, "PLN", idempotencyKey));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        await AssertProblemAsync(
            conflict,
            HttpStatusCode.Conflict,
            ReconciliationApplicationErrors.IdempotencyPayloadConflict.Code);

        await using ReconciliationDbContext reload = _fixture.CreateContext();
        Allocation stored = await reload.Allocations.SingleAsync(
            allocation => allocation.OrganizationId == organizationId);
        Assert.Equal(firstInvoice.Id, stored.InvoiceId);
        Invoice untouched = await reload.Invoices.SingleAsync(candidate => candidate.Id == secondInvoice.Id);
        Assert.Equal(Money.Zero(PersistenceTestData.Pln), untouched.AllocatedAmount);
    }

    [Fact]
    public async Task Exact_match_key_conflicts_with_explicit_amount_and_exact_replay_stays_stable()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 80m, "INV-EXACT");
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(
            organizationId,
            80m,
            reference: "inv-exact");
        await SeedAsync(invoice, bankTransaction);
        string idempotencyKey = Guid.NewGuid().ToString("D");
        using HttpClient client = _factory.CreateApiClient(TestAccessTokens.Create(organizationId.Value));

        HttpResponseMessage created = await client.SendAsync(
            ConfirmRequest(invoice.Id.Value, bankTransaction.Id.Value, idempotencyKey));
        HttpResponseMessage replayed = await client.SendAsync(
            ConfirmRequest(invoice.Id.Value, bankTransaction.Id.Value, idempotencyKey));
        HttpResponseMessage conflict = await client.SendAsync(
            AllocateRequest(invoice.Id.Value, bankTransaction.Id.Value, 80m, "PLN", idempotencyKey));
        HttpResponseMessage replayedAgain = await client.SendAsync(
            ConfirmRequest(invoice.Id.Value, bankTransaction.Id.Value, idempotencyKey));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replayed.StatusCode);
        await AssertProblemAsync(
            conflict,
            HttpStatusCode.Conflict,
            ReconciliationApplicationErrors.IdempotencyPayloadConflict.Code);
        Assert.Equal(HttpStatusCode.OK, replayedAgain.StatusCode);

        using JsonDocument createdDocument = await ReadJsonAsync(created);
        using JsonDocument replayedDocument = await ReadJsonAsync(replayedAgain);
        Assert.False(createdDocument.RootElement.GetProperty("replayed").GetBoolean());
        Assert.True(replayedDocument.RootElement.GetProperty("replayed").GetBoolean());
        Assert.Equal(
            createdDocument.RootElement.GetProperty("allocationId").GetGuid(),
            replayedDocument.RootElement.GetProperty("allocationId").GetGuid());
        Assert.Equal(80m, replayedDocument.RootElement.GetProperty("amount").GetProperty("amount").GetDecimal());

        await using ReconciliationDbContext reload = _fixture.CreateContext();
        Allocation stored = await reload.Allocations.SingleAsync(
            allocation => allocation.OrganizationId == organizationId);
        Assert.Equal(AllocationIdempotencyOperation.ExactMatch, stored.IdempotencyOperation);
        Assert.Equal(new Money(80m, PersistenceTestData.Pln), stored.Amount);
    }

    [Fact]
    public async Task Invalid_amounts_return_400_and_write_nothing()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 1000m);
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(organizationId, 1000m);
        await SeedAsync(invoice, bankTransaction);
        using HttpClient client = _factory.CreateApiClient(TestAccessTokens.Create(organizationId.Value));
        string[] amounts = ["0", "-1", "0.00001", "10.00001", "1000000000000000", "1e30"];

        foreach (string amount in amounts)
        {
            string json = Body(invoice.Id.Value, bankTransaction.Id.Value, "{\"amount\":" + amount + ",\"currency\":\"PLN\"}");
            HttpResponseMessage response = await client.SendAsync(
                AllocateRaw(json, Guid.NewGuid().ToString("D")));
            await AssertProblemAsync(
                response,
                HttpStatusCode.BadRequest,
                ReconciliationApplicationErrors.ExplicitAmountInvalid.Code);
        }

        await AssertUnchangedAsync(organizationId, invoice.Id, bankTransaction.Id);
    }

    [Fact]
    public async Task Malformed_bodies_return_400_and_write_nothing()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 1000m, "INV-JSON");
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(
            organizationId,
            1000m,
            reference: "PAY-JSON");
        await SeedAsync(invoice, bankTransaction);
        using HttpClient client = _factory.CreateApiClient(TestAccessTokens.Create(organizationId.Value));
        string validAmount = "{\"amount\":10,\"currency\":\"PLN\"}";
        string[] payloads =
        [
            "{",
            BodyWithoutInvoice(bankTransaction.Id.Value, validAmount),
            BodyWithoutBank(invoice.Id.Value, validAmount),
            $$"""{"invoiceId":"{{invoice.Id.Value}}","bankTransactionId":"{{bankTransaction.Id.Value}}"}""",
            $$"""{"invoiceId":"{{invoice.Id.Value}}","bankTransactionId":"{{bankTransaction.Id.Value}}","amount":null}""",
            Body(invoice.Id.Value, bankTransaction.Id.Value, "{\"currency\":\"PLN\"}"),
            Body(invoice.Id.Value, bankTransaction.Id.Value, "{\"amount\":\"400\",\"currency\":\"PLN\"}"),
            Body(invoice.Id.Value, bankTransaction.Id.Value, "{\"amount\":10,\"currency\":\"PL\"}"),
            Body(invoice.Id.Value, bankTransaction.Id.Value, "{\"amount\":10,\"currency\":\"EURO\"}"),
            Body(invoice.Id.Value, bankTransaction.Id.Value, "{\"amount\":10,\"currency\":\"\"}"),
            Body(Guid.Empty, bankTransaction.Id.Value, validAmount),
            Body(invoice.Id.Value, Guid.Empty, validAmount),
            ExtraProperty(invoice.Id.Value, bankTransaction.Id.Value, "organizationId", "\"" + organizationId.Value + "\""),
            ExtraProperty(invoice.Id.Value, bankTransaction.Id.Value, "userId", "\"11111111-1111-4111-8111-111111111111\""),
            ExtraProperty(invoice.Id.Value, bankTransaction.Id.Value, "reference", "\"INV-JSON\""),
            ExtraProperty(invoice.Id.Value, bankTransaction.Id.Value, "operation", "\"ExplicitAmount\""),
            ExtraProperty(invoice.Id.Value, bankTransaction.Id.Value, "idempotencyOperation", "2"),
            ExtraProperty(invoice.Id.Value, bankTransaction.Id.Value, "batchId", "\"22222222-2222-4222-8222-222222222222\""),
            ExtraProperty(invoice.Id.Value, bankTransaction.Id.Value, "note", "\"manual\""),
            Body(
                invoice.Id.Value,
                bankTransaction.Id.Value,
                "{\"amount\":10,\"currency\":\"PLN\",\"roundingMode\":\"away\"}"),
        ];

        foreach (string payload in payloads)
        {
            HttpResponseMessage response = await client.SendAsync(
                AllocateRaw(payload, Guid.NewGuid().ToString("D")));
            await AssertProblemAsync(
                response,
                HttpStatusCode.BadRequest,
                ReconciliationApplicationErrors.AllocationInvalidRequest.Code,
                payload);
        }

        await AssertUnchangedAsync(organizationId, invoice.Id, bankTransaction.Id);
    }

    [Fact]
    public async Task Json_numbers_that_would_round_are_rejected_and_write_nothing()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 1000m);
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(organizationId, 1000m);
        await SeedAsync(invoice, bankTransaction);
        using HttpClient client = _factory.CreateApiClient(TestAccessTokens.Create(organizationId.Value));
        string[] amounts =
        [
            "1.0000000000000000000000000000000000000001",
            "400.000000000000000000000000000000000001",
            "1.234500000000000000000000000000000001",
            "0.00001",
            "1e-5",
        ];

        foreach (string amount in amounts)
        {
            HttpResponseMessage response = await client.SendAsync(
                AllocateRaw(
                    AmountTokenBody(invoice.Id.Value, bankTransaction.Id.Value, amount),
                    Guid.NewGuid().ToString("D")));
            await AssertProblemAsync(
                response,
                HttpStatusCode.BadRequest,
                ReconciliationApplicationErrors.ExplicitAmountInvalid.Code,
                amount);
        }

        await AssertUnchangedAsync(organizationId, invoice.Id, bankTransaction.Id);
    }

    [Fact]
    public async Task Exact_json_numbers_keep_their_decimal_value()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 1000m);
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(organizationId, 1000m);
        await SeedAsync(invoice, bankTransaction);
        using HttpClient client = _factory.CreateApiClient(TestAccessTokens.Create(organizationId.Value));
        (string Token, decimal Expected)[] amounts =
        [
            ("0.0001", 0.0001m),
            ("400.00000", 400m),
            ("4e2", 400m),
        ];

        foreach ((string token, decimal expected) in amounts)
        {
            HttpResponseMessage response = await client.SendAsync(
                AllocateRaw(
                    AmountTokenBody(invoice.Id.Value, bankTransaction.Id.Value, token),
                    Guid.NewGuid().ToString("D")));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            using JsonDocument document = await ReadJsonAsync(response);
            Assert.Equal(expected, document.RootElement.GetProperty("amount").GetProperty("amount").GetDecimal());
        }

        await using ReconciliationDbContext reload = _fixture.CreateContext();
        List<Allocation> stored = await reload.Allocations
            .Where(allocation => allocation.OrganizationId == organizationId)
            .ToListAsync();
        Assert.Equal(3, stored.Count);
        Assert.Equal(800.0001m, stored.Sum(allocation => allocation.Amount.Amount));
        Assert.Contains(stored, allocation => allocation.Amount == new Money(0.0001m, PersistenceTestData.Pln));
    }

    [Fact]
    public async Task Duplicate_and_non_object_json_return_400_and_write_nothing()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 1000m);
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(organizationId, 1000m);
        await SeedAsync(invoice, bankTransaction);
        using HttpClient client = _factory.CreateApiClient(TestAccessTokens.Create(organizationId.Value));
        string pair = Body(
            invoice.Id.Value,
            bankTransaction.Id.Value,
            "{\"amount\":10,\"currency\":\"PLN\"}");
        string duplicated = pair.Replace(
            "\"invoiceId\"",
            "\"invoiceId\":\"" + invoice.Id.Value + "\",\"invoiceId\"",
            StringComparison.Ordinal);
        string caseVariant = pair.Replace(
            "\"invoiceId\"",
            "\"InvoiceId\":\"" + invoice.Id.Value + "\",\"invoiceId\"",
            StringComparison.Ordinal);
        string[] payloads =
        [
            string.Empty,
            "[]",
            "null",
            "1",
            "\"text\"",
            AmountTokenBody(invoice.Id.Value, bankTransaction.Id.Value, "10").Replace(
                "\"" + invoice.Id.Value + "\"",
                "\"not-a-guid\"",
                StringComparison.Ordinal),
            duplicated,
            caseVariant,
        ];

        foreach (string payload in payloads)
        {
            HttpResponseMessage response = await client.SendAsync(
                AllocateRaw(payload, Guid.NewGuid().ToString("D")));
            await AssertProblemAsync(
                response,
                HttpStatusCode.BadRequest,
                ReconciliationApplicationErrors.AllocationInvalidRequest.Code,
                payload);
        }

        await AssertUnchangedAsync(organizationId, invoice.Id, bankTransaction.Id);
    }

    [Fact]
    public async Task Request_body_over_4096_bytes_returns_413_and_writes_nothing()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 50m);
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(organizationId, 50m);
        await SeedAsync(invoice, bankTransaction);
        using HttpClient client = _factory.CreateApiClient(TestAccessTokens.Create(organizationId.Value));
        string payload = new('x', 4097);

        HttpResponseMessage response = await client.SendAsync(
            AllocateRaw(payload, Guid.NewGuid().ToString("D")));

        await AssertProblemAsync(
            response,
            HttpStatusCode.RequestEntityTooLarge,
            ReconciliationApplicationErrors.AllocationRequestTooLarge.Code);
        await AssertUnchangedAsync(organizationId, invoice.Id, bankTransaction.Id);
    }

    [Fact]
    public async Task OpenApi_document_describes_the_allocation_request()
    {
        using HttpClient client = _factory.CreateApiClient(TestAccessTokens.Create(Guid.NewGuid()));

        HttpResponseMessage response = await client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument document = await ReadJsonAsync(response);
        JsonElement operation = document.RootElement
            .GetProperty("paths")
            .GetProperty("/api/allocations")
            .GetProperty("post");
        JsonElement requestBody = operation.GetProperty("requestBody");
        Assert.True(requestBody.GetProperty("required").GetBoolean());
        JsonElement schema = requestBody
            .GetProperty("content")
            .GetProperty("application/json")
            .GetProperty("schema");
        Assert.Equal("object", SchemaType(schema));
        Assert.Contains("invoiceId", RequiredProperties(schema));
        Assert.Contains("bankTransactionId", RequiredProperties(schema));
        Assert.Contains("amount", RequiredProperties(schema));
        Assert.Equal("string", SchemaType(schema.GetProperty("properties").GetProperty("invoiceId")));
        Assert.Equal("uuid", schema.GetProperty("properties").GetProperty("invoiceId").GetProperty("format").GetString());
        Assert.Equal("uuid", schema.GetProperty("properties").GetProperty("bankTransactionId").GetProperty("format").GetString());
        JsonElement amount = schema.GetProperty("properties").GetProperty("amount");
        Assert.Equal("object", SchemaType(amount));
        Assert.Contains("amount", RequiredProperties(amount));
        Assert.Contains("currency", RequiredProperties(amount));
        JsonElement amountValue = amount.GetProperty("properties").GetProperty("amount");
        Assert.Equal("number", SchemaType(amountValue));
        Assert.Equal(JsonValueKind.Number, amountValue.GetProperty("multipleOf").ValueKind);
        Assert.Equal(JsonValueKind.Number, amountValue.GetProperty("exclusiveMinimum").ValueKind);
        Assert.Equal(JsonValueKind.Number, amountValue.GetProperty("exclusiveMaximum").ValueKind);
        JsonElement currency = amount.GetProperty("properties").GetProperty("currency");
        Assert.Equal("string", SchemaType(currency));
        Assert.Equal(3, currency.GetProperty("minLength").GetInt32());
        Assert.Equal(3, currency.GetProperty("maxLength").GetInt32());

        bool headerFound = false;
        foreach (JsonElement parameter in operation.GetProperty("parameters").EnumerateArray())
        {
            if (parameter.GetProperty("name").GetString() == "Idempotency-Key"
                && parameter.GetProperty("in").GetString() == "header")
            {
                headerFound = true;
                Assert.True(parameter.GetProperty("required").GetBoolean());
                Assert.Equal("uuid", parameter.GetProperty("schema").GetProperty("format").GetString());
            }
        }

        Assert.True(headerFound);
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/xml")]
    [InlineData(null)]
    public async Task Unsupported_or_missing_media_type_returns_415_and_writes_nothing(string? mediaType)
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 50m);
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(organizationId, 50m);
        await SeedAsync(invoice, bankTransaction);
        using HttpClient client = _factory.CreateApiClient(TestAccessTokens.Create(organizationId.Value));
        string json = Body(
            invoice.Id.Value,
            bankTransaction.Id.Value,
            "{\"amount\":10,\"currency\":\"PLN\"}");

        HttpResponseMessage response = await client.SendAsync(
            AllocateRaw(json, Guid.NewGuid().ToString("D"), mediaType));

        await AssertProblemAsync(
            response,
            HttpStatusCode.UnsupportedMediaType,
            ReconciliationApplicationErrors.AllocationUnsupportedMediaType.Code);
        await AssertUnchangedAsync(organizationId, invoice.Id, bankTransaction.Id);
    }

    [Fact]
    public async Task Missing_or_invalid_idempotency_key_returns_400()
    {
        using HttpClient client = _factory.CreateApiClient(TestAccessTokens.Create(Guid.NewGuid()));
        Guid invoiceId = Guid.NewGuid();
        Guid bankTransactionId = Guid.NewGuid();

        HttpResponseMessage missing = await client.SendAsync(
            AllocateRequest(invoiceId, bankTransactionId, 10m, "PLN", null));
        HttpResponseMessage invalid = await client.SendAsync(
            AllocateRequest(invoiceId, bankTransactionId, 10m, "PLN", "not-a-uuid"));
        HttpResponseMessage empty = await client.SendAsync(
            AllocateRequest(invoiceId, bankTransactionId, 10m, "PLN", Guid.Empty.ToString("D")));
        HttpResponseMessage compact = await client.SendAsync(
            AllocateRequest(invoiceId, bankTransactionId, 10m, "PLN", Guid.NewGuid().ToString("N")));
        HttpRequestMessage duplicate = AllocateRequest(
            invoiceId,
            bankTransactionId,
            10m,
            "PLN",
            Guid.NewGuid().ToString("D"));
        duplicate.Headers.TryAddWithoutValidation("Idempotency-Key", Guid.NewGuid().ToString("D"));
        HttpResponseMessage duplicated = await client.SendAsync(duplicate);

        await AssertProblemAsync(
            missing,
            HttpStatusCode.BadRequest,
            ReconciliationApplicationErrors.IdempotencyKeyRequired.Code);
        await AssertProblemAsync(
            invalid,
            HttpStatusCode.BadRequest,
            ReconciliationApplicationErrors.IdempotencyKeyInvalid.Code);
        await AssertProblemAsync(
            empty,
            HttpStatusCode.BadRequest,
            ReconciliationApplicationErrors.IdempotencyKeyInvalid.Code);
        await AssertProblemAsync(
            compact,
            HttpStatusCode.BadRequest,
            ReconciliationApplicationErrors.IdempotencyKeyInvalid.Code);
        await AssertProblemAsync(
            duplicated,
            HttpStatusCode.BadRequest,
            ReconciliationApplicationErrors.IdempotencyKeyInvalid.Code);
    }

    [Fact]
    public async Task Currency_mismatch_returns_409_and_writes_nothing()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 100m);
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(organizationId, 100m);
        await SeedAsync(invoice, bankTransaction);
        using HttpClient client = _factory.CreateApiClient(TestAccessTokens.Create(organizationId.Value));

        HttpResponseMessage response = await client.SendAsync(
            AllocateRequest(
                invoice.Id.Value,
                bankTransaction.Id.Value,
                10m,
                "EUR",
                Guid.NewGuid().ToString("D")));

        string body = await AssertProblemAsync(
            response,
            HttpStatusCode.Conflict,
            ReconciliationErrors.CurrencyMismatch.Code);
        Assert.DoesNotContain("SqlException", body, StringComparison.Ordinal);
        await AssertUnchangedAsync(organizationId, invoice.Id, bankTransaction.Id);
    }

    [Fact]
    public async Task Amount_above_invoice_outstanding_returns_409_and_writes_nothing()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 100m);
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(organizationId, 500m);
        await SeedAsync(invoice, bankTransaction);
        using HttpClient client = _factory.CreateApiClient(TestAccessTokens.Create(organizationId.Value));

        HttpResponseMessage response = await client.SendAsync(
            AllocateRequest(
                invoice.Id.Value,
                bankTransaction.Id.Value,
                150m,
                "PLN",
                Guid.NewGuid().ToString("D")));

        await AssertProblemAsync(
            response,
            HttpStatusCode.Conflict,
            ReconciliationErrors.InvoiceAllocationExceedsOutstanding.Code);
        await AssertUnchangedAsync(organizationId, invoice.Id, bankTransaction.Id);
    }

    [Fact]
    public async Task Amount_above_unapplied_bank_amount_returns_409_and_writes_nothing()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 500m);
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(organizationId, 100m);
        await SeedAsync(invoice, bankTransaction);
        using HttpClient client = _factory.CreateApiClient(TestAccessTokens.Create(organizationId.Value));

        HttpResponseMessage response = await client.SendAsync(
            AllocateRequest(
                invoice.Id.Value,
                bankTransaction.Id.Value,
                150m,
                "PLN",
                Guid.NewGuid().ToString("D")));

        await AssertProblemAsync(
            response,
            HttpStatusCode.Conflict,
            ReconciliationErrors.BankTransactionAllocationExceedsUnapplied.Code);
        await AssertUnchangedAsync(organizationId, invoice.Id, bankTransaction.Id);
    }

    [Fact]
    public async Task Concurrency_conflict_returns_409_and_persists_nothing()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 40m);
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(organizationId, 40m);
        await SeedAsync(invoice, bankTransaction);
        using ConflictApiFactory factory = new(_fixture.ConnectionString);
        using HttpClient client = factory.CreateApiClient(TestAccessTokens.Create(organizationId.Value));

        HttpResponseMessage response = await client.SendAsync(
            AllocateRequest(
                invoice.Id.Value,
                bankTransaction.Id.Value,
                10m,
                "PLN",
                Guid.NewGuid().ToString("D")));

        string body = await AssertProblemAsync(
            response,
            HttpStatusCode.Conflict,
            ReconciliationApplicationErrors.ConcurrencyConflict.Code);
        Assert.DoesNotContain("rowVersion", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SqlException", body, StringComparison.Ordinal);
        await AssertUnchangedAsync(organizationId, invoice.Id, bankTransaction.Id);
    }

    [Fact]
    public async Task Missing_token_returns_401()
    {
        using HttpClient client = _factory.CreateApiClient();

        HttpResponseMessage response = await client.SendAsync(
            AllocateRequest(Guid.NewGuid(), Guid.NewGuid(), 10m, "PLN", Guid.NewGuid().ToString("D")));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Invalid_token_returns_401()
    {
        using HttpClient client = _factory.CreateApiClient(
            TestAccessTokens.CreateSignedWithOtherKey(Guid.NewGuid()));

        HttpResponseMessage response = await client.SendAsync(
            AllocateRequest(Guid.NewGuid(), Guid.NewGuid(), 10m, "PLN", Guid.NewGuid().ToString("D")));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Missing_organization_claim_returns_403()
    {
        using HttpClient client = _factory.CreateApiClient(
            TestAccessTokens.CreateWithoutOrganizationClaim());

        HttpResponseMessage response = await client.SendAsync(
            AllocateRequest(Guid.NewGuid(), Guid.NewGuid(), 10m, "PLN", Guid.NewGuid().ToString("D")));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Unusable_organization_claim_returns_403(string organizationClaim)
    {
        using HttpClient client = _factory.CreateApiClient(
            TestAccessTokens.CreateWithOrganizationClaim(organizationClaim));

        HttpResponseMessage response = await client.SendAsync(
            AllocateRequest(Guid.NewGuid(), Guid.NewGuid(), 10m, "PLN", Guid.NewGuid().ToString("D")));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Foreign_invoice_returns_404_and_creates_no_allocation()
    {
        OrganizationId owner = OrganizationId.New();
        OrganizationId caller = OrganizationId.New();
        Invoice foreignInvoice = PersistenceTestData.CreateInvoice(owner, 1234.56m, "FOREIGN-INV-8841");
        BankTransaction localBank = PersistenceTestData.CreateBankTransaction(
            caller,
            100m,
            reference: "LOCAL-PAY");
        await SeedAsync(foreignInvoice, localBank);
        using HttpClient client = _factory.CreateApiClient(TestAccessTokens.Create(caller.Value));

        HttpResponseMessage response = await client.SendAsync(
            AllocateRequest(
                foreignInvoice.Id.Value,
                localBank.Id.Value,
                10m,
                "PLN",
                Guid.NewGuid().ToString("D")));

        string body = await AssertProblemAsync(
            response,
            HttpStatusCode.NotFound,
            ReconciliationApplicationErrors.InvoiceNotFound.Code);
        AssertNoForeignDetails(body, owner, "FOREIGN-INV-8841", "1234.56");

        await using ReconciliationDbContext reload = _fixture.CreateContext();
        Assert.Equal(
            0,
            await reload.Allocations.CountAsync(
                allocation => allocation.InvoiceId == foreignInvoice.Id
                    || allocation.BankTransactionId == localBank.Id));
        Invoice storedInvoice = await reload.Invoices.SingleAsync(candidate => candidate.Id == foreignInvoice.Id);
        BankTransaction storedBank = await reload.BankTransactions.SingleAsync(
            candidate => candidate.Id == localBank.Id);
        Assert.Equal(Money.Zero(PersistenceTestData.Pln), storedInvoice.AllocatedAmount);
        Assert.Equal(Money.Zero(PersistenceTestData.Pln), storedBank.AllocatedAmount);
    }

    [Fact]
    public async Task Foreign_bank_transaction_returns_404_and_creates_no_allocation()
    {
        OrganizationId owner = OrganizationId.New();
        OrganizationId caller = OrganizationId.New();
        Invoice localInvoice = PersistenceTestData.CreateInvoice(caller, 100m, "LOCAL-INV");
        BankTransaction foreignBank = PersistenceTestData.CreateBankTransaction(
            owner,
            4321.09m,
            reference: "FOREIGN-PAY-5520");
        await SeedAsync(localInvoice, foreignBank);
        using HttpClient client = _factory.CreateApiClient(TestAccessTokens.Create(caller.Value));

        HttpResponseMessage response = await client.SendAsync(
            AllocateRequest(
                localInvoice.Id.Value,
                foreignBank.Id.Value,
                10m,
                "PLN",
                Guid.NewGuid().ToString("D")));

        string body = await AssertProblemAsync(
            response,
            HttpStatusCode.NotFound,
            ReconciliationApplicationErrors.BankTransactionNotFound.Code);
        AssertNoForeignDetails(body, owner, "FOREIGN-PAY-5520", "4321.09");

        await using ReconciliationDbContext reload = _fixture.CreateContext();
        Assert.Equal(
            0,
            await reload.Allocations.CountAsync(allocation => allocation.OrganizationId == caller));
        Assert.Equal(
            0,
            await reload.Allocations.CountAsync(allocation => allocation.OrganizationId == owner));
        Invoice storedInvoice = await reload.Invoices.SingleAsync(candidate => candidate.Id == localInvoice.Id);
        BankTransaction storedBank = await reload.BankTransactions.SingleAsync(
            candidate => candidate.Id == foreignBank.Id);
        Assert.Equal(Money.Zero(PersistenceTestData.Pln), storedInvoice.AllocatedAmount);
        Assert.Equal(Money.Zero(PersistenceTestData.Pln), storedBank.AllocatedAmount);
    }

    [Fact]
    public async Task Organization_query_and_header_cannot_switch_tenant()
    {
        OrganizationId organizationA = OrganizationId.New();
        OrganizationId organizationB = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationA, 30m, "INV-A");
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(
            organizationA,
            30m,
            reference: "PAY-A");
        await SeedAsync(invoice, bankTransaction);
        using HttpClient client = _factory.CreateApiClient(TestAccessTokens.Create(organizationA.Value));
        client.DefaultRequestHeaders.Add("X-Organization-Id", organizationB.Value.ToString());

        HttpResponseMessage response = await client.SendAsync(
            AllocateRequest(
                invoice.Id.Value,
                bankTransaction.Id.Value,
                30m,
                "PLN",
                Guid.NewGuid().ToString("D"),
                $"/api/allocations?organizationId={organizationB.Value}"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        await using ReconciliationDbContext reload = _fixture.CreateContext();
        Allocation stored = await reload.Allocations.SingleAsync(
            allocation => allocation.InvoiceId == invoice.Id);
        Assert.Equal(organizationA, stored.OrganizationId);
        Assert.Equal(
            0,
            await reload.Allocations.CountAsync(allocation => allocation.OrganizationId == organizationB));
    }

    [Fact]
    public async Task Idempotency_keys_are_independent_across_organizations()
    {
        OrganizationId organizationA = OrganizationId.New();
        OrganizationId organizationB = OrganizationId.New();
        Invoice invoiceA = PersistenceTestData.CreateInvoice(organizationA, 100m, "INV-A");
        Invoice invoiceB = PersistenceTestData.CreateInvoice(organizationB, 80m, "INV-B");
        BankTransaction bankA = PersistenceTestData.CreateBankTransaction(
            organizationA,
            100m,
            reference: "PAY-A");
        BankTransaction bankB = PersistenceTestData.CreateBankTransaction(
            organizationB,
            80m,
            reference: "PAY-B");
        await SeedAsync(invoiceA, invoiceB, bankA, bankB);
        string idempotencyKey = Guid.NewGuid().ToString("D");
        using HttpClient clientA = _factory.CreateApiClient(TestAccessTokens.Create(organizationA.Value));
        using HttpClient clientB = _factory.CreateApiClient(TestAccessTokens.Create(organizationB.Value));

        HttpResponseMessage createdA = await clientA.SendAsync(
            AllocateRequest(invoiceA.Id.Value, bankA.Id.Value, 40m, "PLN", idempotencyKey));
        HttpResponseMessage createdB = await clientB.SendAsync(
            AllocateRequest(invoiceB.Id.Value, bankB.Id.Value, 15m, "PLN", idempotencyKey));
        HttpResponseMessage replayedA = await clientA.SendAsync(
            AllocateRequest(invoiceA.Id.Value, bankA.Id.Value, 40m, "PLN", idempotencyKey));
        HttpResponseMessage replayedB = await clientB.SendAsync(
            AllocateRequest(invoiceB.Id.Value, bankB.Id.Value, 15m, "PLN", idempotencyKey));

        Assert.Equal(HttpStatusCode.Created, createdA.StatusCode);
        Assert.Equal(HttpStatusCode.Created, createdB.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replayedA.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replayedB.StatusCode);
        using JsonDocument documentA = await ReadJsonAsync(createdA);
        using JsonDocument documentB = await ReadJsonAsync(createdB);
        using JsonDocument replayA = await ReadJsonAsync(replayedA);
        using JsonDocument replayB = await ReadJsonAsync(replayedB);
        Guid allocationA = documentA.RootElement.GetProperty("allocationId").GetGuid();
        Guid allocationB = documentB.RootElement.GetProperty("allocationId").GetGuid();
        Assert.NotEqual(allocationA, allocationB);
        Assert.Equal(allocationA, replayA.RootElement.GetProperty("allocationId").GetGuid());
        Assert.Equal(allocationB, replayB.RootElement.GetProperty("allocationId").GetGuid());
        Assert.Equal(40m, replayA.RootElement.GetProperty("amount").GetProperty("amount").GetDecimal());
        Assert.Equal(15m, replayB.RootElement.GetProperty("amount").GetProperty("amount").GetDecimal());

        await using ReconciliationDbContext reload = _fixture.CreateContext();
        Allocation storedA = await reload.Allocations.SingleAsync(
            allocation => allocation.OrganizationId == organizationA);
        Allocation storedB = await reload.Allocations.SingleAsync(
            allocation => allocation.OrganizationId == organizationB);
        Assert.Equal(Guid.Parse(idempotencyKey), storedA.IdempotencyKey);
        Assert.Equal(Guid.Parse(idempotencyKey), storedB.IdempotencyKey);
    }

    [Fact]
    public async Task One_invoice_can_be_paid_by_three_independent_bank_transactions()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 1000m, "INV-A");
        BankTransaction first = PersistenceTestData.CreateBankTransaction(
            organizationId,
            300m,
            reference: "PAY-1");
        BankTransaction second = PersistenceTestData.CreateBankTransaction(
            organizationId,
            400m,
            reference: "PAY-2");
        BankTransaction third = PersistenceTestData.CreateBankTransaction(
            organizationId,
            300m,
            reference: "PAY-3");
        await SeedAsync(invoice, first, second, third);
        using HttpClient client = _factory.CreateApiClient(TestAccessTokens.Create(organizationId.Value));

        HttpResponseMessage firstResponse = await client.SendAsync(
            AllocateRequest(invoice.Id.Value, first.Id.Value, 300m, "PLN", Guid.NewGuid().ToString("D")));
        HttpResponseMessage secondResponse = await client.SendAsync(
            AllocateRequest(invoice.Id.Value, second.Id.Value, 400m, "PLN", Guid.NewGuid().ToString("D")));
        HttpResponseMessage thirdResponse = await client.SendAsync(
            AllocateRequest(invoice.Id.Value, third.Id.Value, 300m, "PLN", Guid.NewGuid().ToString("D")));

        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, secondResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, thirdResponse.StatusCode);

        await using ReconciliationDbContext reload = _fixture.CreateContext();
        List<Allocation> allocations = await reload.Allocations
            .Where(allocation => allocation.OrganizationId == organizationId)
            .ToListAsync();
        Assert.Equal(3, allocations.Count);
        Assert.Equal(3, allocations.Select(allocation => allocation.Id).Distinct().Count());
        Assert.All(allocations, allocation => Assert.Equal(organizationId, allocation.OrganizationId));
        Assert.Equal(1000m, allocations.Sum(allocation => allocation.Amount.Amount));
        Assert.Equal(
            new[] { 300m, 300m, 400m },
            allocations.Select(allocation => allocation.Amount.Amount).OrderBy(amount => amount).ToArray());

        Invoice storedInvoice = await reload.Invoices.SingleAsync(candidate => candidate.Id == invoice.Id);
        Assert.Equal(Money.Zero(PersistenceTestData.Pln), storedInvoice.OutstandingAmount);
        await AssertFullyAllocatedAsync(reload, first);
        await AssertFullyAllocatedAsync(reload, second);
        await AssertFullyAllocatedAsync(reload, third);
    }

    [Fact]
    public async Task One_bank_transaction_can_be_allocated_across_three_invoices()
    {
        OrganizationId organizationId = OrganizationId.New();
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(
            organizationId,
            1500m,
            reference: "PAY-X");
        Invoice invoiceA = PersistenceTestData.CreateInvoice(organizationId, 500m, "INV-A");
        Invoice invoiceB = PersistenceTestData.CreateInvoice(organizationId, 700m, "INV-B");
        Invoice invoiceC = PersistenceTestData.CreateInvoice(organizationId, 300m, "INV-C");
        await SeedAsync(bankTransaction, invoiceA, invoiceB, invoiceC);
        using HttpClient client = _factory.CreateApiClient(TestAccessTokens.Create(organizationId.Value));

        HttpResponseMessage responseA = await client.SendAsync(
            AllocateRequest(invoiceA.Id.Value, bankTransaction.Id.Value, 500m, "PLN", Guid.NewGuid().ToString("D")));
        HttpResponseMessage responseB = await client.SendAsync(
            AllocateRequest(invoiceB.Id.Value, bankTransaction.Id.Value, 700m, "PLN", Guid.NewGuid().ToString("D")));
        HttpResponseMessage responseC = await client.SendAsync(
            AllocateRequest(invoiceC.Id.Value, bankTransaction.Id.Value, 300m, "PLN", Guid.NewGuid().ToString("D")));

        Assert.Equal(HttpStatusCode.Created, responseA.StatusCode);
        Assert.Equal(HttpStatusCode.Created, responseB.StatusCode);
        Assert.Equal(HttpStatusCode.Created, responseC.StatusCode);

        await using ReconciliationDbContext reload = _fixture.CreateContext();
        List<Allocation> allocations = await reload.Allocations
            .Where(allocation => allocation.OrganizationId == organizationId)
            .ToListAsync();
        Assert.Equal(3, allocations.Count);
        Assert.Equal(3, allocations.Select(allocation => allocation.InvoiceId).Distinct().Count());
        Assert.All(allocations, allocation => Assert.Equal(bankTransaction.Id, allocation.BankTransactionId));
        Assert.Equal(1500m, allocations.Sum(allocation => allocation.Amount.Amount));

        BankTransaction storedBank = await reload.BankTransactions.SingleAsync(
            candidate => candidate.Id == bankTransaction.Id);
        Assert.Equal(Money.Zero(PersistenceTestData.Pln), storedBank.UnappliedAmount);
        await AssertInvoiceSettledAsync(reload, invoiceA);
        await AssertInvoiceSettledAsync(reload, invoiceB);
        await AssertInvoiceSettledAsync(reload, invoiceC);
    }

    [Fact]
    public async Task A_later_rejected_allocation_leaves_earlier_allocations_committed()
    {
        OrganizationId organizationId = OrganizationId.New();
        OrganizationId bystanderOrganizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 1000m, "INV-SEQ");
        Invoice unrelated = PersistenceTestData.CreateInvoice(organizationId, 50m, "INV-OTHER");
        Invoice bystanderInvoice = PersistenceTestData.CreateInvoice(bystanderOrganizationId, 75m, "INV-BY");
        BankTransaction first = PersistenceTestData.CreateBankTransaction(
            organizationId,
            400m,
            reference: "PAY-1");
        BankTransaction second = PersistenceTestData.CreateBankTransaction(
            organizationId,
            400m,
            reference: "PAY-2");
        BankTransaction third = PersistenceTestData.CreateBankTransaction(
            organizationId,
            400m,
            reference: "PAY-3");
        BankTransaction bystanderBank = PersistenceTestData.CreateBankTransaction(
            bystanderOrganizationId,
            75m,
            reference: "PAY-BY");
        await SeedAsync(
            invoice,
            unrelated,
            bystanderInvoice,
            first,
            second,
            third,
            bystanderBank);
        string failedKey = Guid.NewGuid().ToString("D");
        using HttpClient client = _factory.CreateApiClient(TestAccessTokens.Create(organizationId.Value));

        HttpResponseMessage firstResponse = await client.SendAsync(
            AllocateRequest(invoice.Id.Value, first.Id.Value, 400m, "PLN", Guid.NewGuid().ToString("D")));
        HttpResponseMessage secondResponse = await client.SendAsync(
            AllocateRequest(invoice.Id.Value, second.Id.Value, 400m, "PLN", Guid.NewGuid().ToString("D")));
        HttpResponseMessage failed = await client.SendAsync(
            AllocateRequest(invoice.Id.Value, third.Id.Value, 400m, "PLN", failedKey));

        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, secondResponse.StatusCode);
        string failureBody = await AssertProblemAsync(
            failed,
            HttpStatusCode.Conflict,
            ReconciliationErrors.InvoiceAllocationExceedsOutstanding.Code);
        Assert.DoesNotContain("rowVersion", failureBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SqlException", failureBody, StringComparison.Ordinal);

        await using (ReconciliationDbContext reload = _fixture.CreateContext())
        {
            Assert.Equal(
                2,
                await reload.Allocations.CountAsync(allocation => allocation.OrganizationId == organizationId));
            Assert.False(await reload.Allocations.AnyAsync(
                allocation => allocation.IdempotencyKey == Guid.Parse(failedKey)));
            Invoice storedInvoice = await reload.Invoices.SingleAsync(candidate => candidate.Id == invoice.Id);
            BankTransaction storedThird = await reload.BankTransactions.SingleAsync(
                candidate => candidate.Id == third.Id);
            Invoice storedUnrelated = await reload.Invoices.SingleAsync(candidate => candidate.Id == unrelated.Id);
            Assert.Equal(new Money(800m, PersistenceTestData.Pln), storedInvoice.AllocatedAmount);
            Assert.Equal(new Money(200m, PersistenceTestData.Pln), storedInvoice.OutstandingAmount);
            Assert.Equal(Money.Zero(PersistenceTestData.Pln), storedThird.AllocatedAmount);
            Assert.Equal(Money.Zero(PersistenceTestData.Pln), storedUnrelated.AllocatedAmount);
            Assert.Equal(
                0,
                await reload.Allocations.CountAsync(
                    allocation => allocation.OrganizationId == bystanderOrganizationId));
        }

        HttpResponseMessage repeatedFailure = await client.SendAsync(
            AllocateRequest(invoice.Id.Value, third.Id.Value, 400m, "PLN", failedKey));
        await AssertProblemAsync(
            repeatedFailure,
            HttpStatusCode.Conflict,
            ReconciliationErrors.InvoiceAllocationExceedsOutstanding.Code);

        HttpResponseMessage recovered = await client.SendAsync(
            AllocateRequest(invoice.Id.Value, third.Id.Value, 200m, "PLN", failedKey));

        Assert.Equal(HttpStatusCode.Created, recovered.StatusCode);
        using JsonDocument recoveredDocument = await ReadJsonAsync(recovered);
        Assert.False(recoveredDocument.RootElement.GetProperty("replayed").GetBoolean());
        Assert.Equal(200m, recoveredDocument.RootElement.GetProperty("amount").GetProperty("amount").GetDecimal());

        await using ReconciliationDbContext finalReload = _fixture.CreateContext();
        List<Allocation> allocations = await finalReload.Allocations
            .Where(allocation => allocation.OrganizationId == organizationId)
            .ToListAsync();
        Assert.Equal(3, allocations.Count);
        Assert.Equal(1000m, allocations.Sum(allocation => allocation.Amount.Amount));
        Assert.Contains(allocations, allocation => allocation.IdempotencyKey == Guid.Parse(failedKey)
            && allocation.Amount == new Money(200m, PersistenceTestData.Pln));
        Invoice finalInvoice = await finalReload.Invoices.SingleAsync(candidate => candidate.Id == invoice.Id);
        BankTransaction finalThird = await finalReload.BankTransactions.SingleAsync(
            candidate => candidate.Id == third.Id);
        Invoice finalUnrelated = await finalReload.Invoices.SingleAsync(candidate => candidate.Id == unrelated.Id);
        Invoice finalBystander = await finalReload.Invoices.SingleAsync(
            candidate => candidate.Id == bystanderInvoice.Id);
        Assert.Equal(Money.Zero(PersistenceTestData.Pln), finalInvoice.OutstandingAmount);
        Assert.Equal(new Money(200m, PersistenceTestData.Pln), finalThird.AllocatedAmount);
        Assert.Equal(new Money(200m, PersistenceTestData.Pln), finalThird.UnappliedAmount);
        Assert.Equal(Money.Zero(PersistenceTestData.Pln), finalUnrelated.AllocatedAmount);
        Assert.Equal(Money.Zero(PersistenceTestData.Pln), finalBystander.AllocatedAmount);
    }

    public void Dispose()
    {
        _factory.Dispose();
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

    private async Task AssertUnchangedAsync(
        OrganizationId organizationId,
        InvoiceId invoiceId,
        BankTransactionId bankTransactionId)
    {
        await using ReconciliationDbContext reload = _fixture.CreateContext();
        Invoice invoice = await reload.Invoices.SingleAsync(candidate => candidate.Id == invoiceId);
        BankTransaction bankTransaction = await reload.BankTransactions.SingleAsync(
            candidate => candidate.Id == bankTransactionId);
        Assert.Equal(Money.Zero(PersistenceTestData.Pln), invoice.AllocatedAmount);
        Assert.Equal(Money.Zero(PersistenceTestData.Pln), bankTransaction.AllocatedAmount);
        Assert.Equal(
            0,
            await reload.Allocations.CountAsync(allocation => allocation.OrganizationId == organizationId));
    }

    private static async Task AssertFullyAllocatedAsync(
        ReconciliationDbContext reload,
        BankTransaction bankTransaction)
    {
        BankTransaction stored = await reload.BankTransactions.SingleAsync(
            candidate => candidate.Id == bankTransaction.Id);
        Assert.Equal(bankTransaction.Amount, stored.AllocatedAmount);
        Assert.Equal(Money.Zero(PersistenceTestData.Pln), stored.UnappliedAmount);
    }

    private static async Task AssertInvoiceSettledAsync(ReconciliationDbContext reload, Invoice invoice)
    {
        Invoice stored = await reload.Invoices.SingleAsync(candidate => candidate.Id == invoice.Id);
        Assert.Equal(Money.Zero(PersistenceTestData.Pln), stored.OutstandingAmount);
    }

    private static void AssertNoForeignDetails(
        string body,
        OrganizationId owner,
        string foreignReference,
        string foreignAmount)
    {
        Assert.DoesNotContain(foreignReference, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(foreignAmount, body, StringComparison.Ordinal);
        Assert.DoesNotContain(owner.Value.ToString(), body, StringComparison.OrdinalIgnoreCase);
    }

    private static string AmountTokenBody(Guid invoiceId, Guid bankTransactionId, string amountToken)
    {
        return "{\"invoiceId\":\""
            + invoiceId
            + "\",\"bankTransactionId\":\""
            + bankTransactionId
            + "\",\"amount\":{\"amount\":"
            + amountToken
            + ",\"currency\":\"PLN\"}}";
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

    private static string Body(Guid invoiceId, Guid bankTransactionId, string amountJson)
    {
        return $$"""{"invoiceId":"{{invoiceId}}","bankTransactionId":"{{bankTransactionId}}","amount":{{amountJson}}}""";
    }

    private static string BodyWithoutInvoice(Guid bankTransactionId, string amountJson)
    {
        return $$"""{"bankTransactionId":"{{bankTransactionId}}","amount":{{amountJson}}}""";
    }

    private static string BodyWithoutBank(Guid invoiceId, string amountJson)
    {
        return $$"""{"invoiceId":"{{invoiceId}}","amount":{{amountJson}}}""";
    }

    private static string ExtraProperty(
        Guid invoiceId,
        Guid bankTransactionId,
        string propertyName,
        string propertyValue)
    {
        return $$"""{"invoiceId":"{{invoiceId}}","bankTransactionId":"{{bankTransactionId}}","{{propertyName}}":{{propertyValue}},"amount":{"amount":10,"currency":"PLN"}"""
            + "}";
    }

    private static HttpRequestMessage AllocateRequest(
        Guid invoiceId,
        Guid bankTransactionId,
        decimal amount,
        string currency,
        string? idempotencyKey,
        string path = "/api/allocations")
    {
        string amountText = amount.ToString("0.00", CultureInfo.InvariantCulture);
        string json = Body(
            invoiceId,
            bankTransactionId,
            "{\"amount\":" + amountText + ",\"currency\":\"" + currency + "\"}");
        return AllocateRaw(json, idempotencyKey, "application/json", path);
    }

    private static HttpRequestMessage AllocateRaw(
        string json,
        string? idempotencyKey,
        string? mediaType = "application/json",
        string path = "/api/allocations")
    {
        HttpContent content = mediaType is null
            ? new ByteArrayContent(Encoding.UTF8.GetBytes(json))
            : new StringContent(json, Encoding.UTF8, mediaType);
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = content,
        };

        if (idempotencyKey is not null)
        {
            request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        }

        return request;
    }

    private static HttpRequestMessage ConfirmRequest(
        Guid invoiceId,
        Guid bankTransactionId,
        string idempotencyKey)
    {
        string json = $$"""{"invoiceId":"{{invoiceId}}","bankTransactionId":"{{bankTransactionId}}"}""";
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/matches/confirm")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        return request;
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
        Assert.True(response.StatusCode == statusCode, $"Expected {(int)statusCode} but got {(int)response.StatusCode}. {because}");
        Assert.True(
            response.Content.Headers.ContentType?.MediaType == "application/problem+json",
            $"Content-Type: {response.Content.Headers.ContentType}. {because}");
        using JsonDocument document = JsonDocument.Parse(body);
        Assert.Equal((int)statusCode, document.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("detail").GetString()));
        return body;
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        string json = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(json);
    }

    private sealed class ConflictApiFactory : FinOpsDeskApiFactory
    {
        public ConflictApiFactory(string connectionString)
            : base(connectionString)
        {
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IUnitOfWork>();
                services.AddScoped<IUnitOfWork, ConflictingUnitOfWork>();
            });
        }

        private sealed class ConflictingUnitOfWork : IUnitOfWork
        {
            public Task SaveChangesAsync(CancellationToken cancellationToken)
            {
                throw new ConcurrencyConflictException(
                    "The persisted data was modified by another operation.");
            }
        }
    }
}