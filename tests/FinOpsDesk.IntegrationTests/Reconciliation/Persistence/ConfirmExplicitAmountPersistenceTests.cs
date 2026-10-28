using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Application.Allocations;
using FinOpsDesk.Modules.Reconciliation.Application.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Application.FinancialAudit;
using FinOpsDesk.Modules.Reconciliation.Application.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FinOpsDesk.IntegrationTests.Reconciliation.Persistence;

[Collection(SqlServerCollection.Name)]
public sealed class ConfirmExplicitAmountPersistenceTests
{
    private static readonly DateTimeOffset ConfirmedAt =
        new(2026, 10, 26, 16, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly BookingDate = new(2026, 8, 24);
    private static readonly CurrencyCode Eur = new("EUR");

    private readonly SqlServerFixture _fixture;

    public ConfirmExplicitAmountPersistenceTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Explicit_amount_persists_the_operation_and_both_open_balances()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 1000m, "INV-001");
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(
            organizationId,
            600m,
            reference: "UNRELATED");
        Guid idempotencyKey = Guid.NewGuid();
        await SeedAsync(invoice, bankTransaction);

        Result<ConfirmExplicitAmountResponse> result = await ConfirmAsync(
            organizationId,
            invoice.Id,
            bankTransaction.Id,
            400m,
            idempotencyKey);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.False(result.Value.Replayed);
        Assert.Equal(400m, result.Value.Amount.Amount);

        await using ReconciliationDbContext reload = _fixture.CreateContext();
        Allocation stored = await reload.Allocations.SingleAsync(
            allocation => allocation.OrganizationId == organizationId);
        Invoice storedInvoice = await reload.Invoices.SingleAsync(candidate => candidate.Id == invoice.Id);
        BankTransaction storedBankTransaction = await reload.BankTransactions.SingleAsync(
            candidate => candidate.Id == bankTransaction.Id);

        Assert.Equal(result.Value.AllocationId, stored.Id.Value);
        Assert.Equal(idempotencyKey, stored.IdempotencyKey);
        Assert.Equal(AllocationIdempotencyOperation.ExplicitAmount, stored.IdempotencyOperation);
        Assert.Equal(new Money(400m, PersistenceTestData.Pln), stored.Amount);
        Assert.Equal(new Money(600m, PersistenceTestData.Pln), storedInvoice.OutstandingAmount);
        Assert.Equal(new Money(200m, PersistenceTestData.Pln), storedBankTransaction.UnappliedAmount);
    }

    [Fact]
    public async Task Replay_returns_the_original_allocation_without_changing_balances()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 1000m);
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(organizationId, 1000m);
        Guid idempotencyKey = Guid.NewGuid();
        await SeedAsync(invoice, bankTransaction);

        Result<ConfirmExplicitAmountResponse> first = await ConfirmAsync(
            organizationId,
            invoice.Id,
            bankTransaction.Id,
            250m,
            idempotencyKey);
        byte[] invoiceVersion = await RowVersionAsync(invoice.Id);
        Result<ConfirmExplicitAmountResponse> second = await ConfirmAsync(
            organizationId,
            invoice.Id,
            bankTransaction.Id,
            250m,
            idempotencyKey);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.NotNull(first.Value);
        Assert.NotNull(second.Value);
        Assert.False(first.Value.Replayed);
        Assert.True(second.Value.Replayed);
        Assert.Equal(first.Value.AllocationId, second.Value.AllocationId);
        Assert.Equal(250m, second.Value.Amount.Amount);
        Assert.Equal(invoiceVersion, await RowVersionAsync(invoice.Id));
    }

    [Fact]
    public async Task Same_pair_can_receive_another_allocation_with_a_different_key()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 1000m);
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(organizationId, 1000m);
        await SeedAsync(invoice, bankTransaction);

        Result<ConfirmExplicitAmountResponse> first = await ConfirmAsync(
            organizationId,
            invoice.Id,
            bankTransaction.Id,
            400m,
            Guid.NewGuid());
        Result<ConfirmExplicitAmountResponse> second = await ConfirmAsync(
            organizationId,
            invoice.Id,
            bankTransaction.Id,
            200m,
            Guid.NewGuid());

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.NotNull(first.Value);
        Assert.NotNull(second.Value);
        Assert.NotEqual(first.Value.AllocationId, second.Value.AllocationId);

        await using ReconciliationDbContext reload = _fixture.CreateContext();
        Invoice storedInvoice = await reload.Invoices.SingleAsync(candidate => candidate.Id == invoice.Id);
        BankTransaction storedBankTransaction = await reload.BankTransactions.SingleAsync(
            candidate => candidate.Id == bankTransaction.Id);
        Assert.Equal(new Money(600m, PersistenceTestData.Pln), storedInvoice.AllocatedAmount);
        Assert.Equal(new Money(400m, PersistenceTestData.Pln), storedBankTransaction.UnappliedAmount);
        Assert.Equal(
            2,
            await reload.Allocations.CountAsync(allocation => allocation.OrganizationId == organizationId));
    }

    [Fact]
    public async Task Same_key_with_a_different_amount_does_not_write_again()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 1000m);
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(organizationId, 1000m);
        Guid idempotencyKey = Guid.NewGuid();
        await SeedAsync(invoice, bankTransaction);

        Assert.True((await ConfirmAsync(
            organizationId,
            invoice.Id,
            bankTransaction.Id,
            400m,
            idempotencyKey)).IsSuccess);
        Result<ConfirmExplicitAmountResponse> second = await ConfirmAsync(
            organizationId,
            invoice.Id,
            bankTransaction.Id,
            100m,
            idempotencyKey);

        Assert.Equal(
            ReconciliationApplicationErrors.IdempotencyPayloadConflict.Code,
            second.Error?.Code);

        await using ReconciliationDbContext reload = _fixture.CreateContext();
        Invoice storedInvoice = await reload.Invoices.SingleAsync(candidate => candidate.Id == invoice.Id);
        Assert.Equal(new Money(400m, PersistenceTestData.Pln), storedInvoice.AllocatedAmount);
        Assert.Equal(
            1,
            await reload.Allocations.CountAsync(allocation => allocation.OrganizationId == organizationId));
    }

    [Fact]
    public async Task Same_key_with_a_different_currency_does_not_write_again()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice plnInvoice = PersistenceTestData.CreateInvoice(organizationId, 400m);
        BankTransaction plnBankTransaction = PersistenceTestData.CreateBankTransaction(organizationId, 400m);
        Invoice eurInvoice = new(
            InvoiceId.New(),
            organizationId,
            "INV-EUR",
            new Money(400m, Eur));
        BankTransaction eurBankTransaction = new(
            BankTransactionId.New(),
            organizationId,
            new Money(400m, Eur),
            BookingDate,
            "INV-EUR");
        Guid idempotencyKey = Guid.NewGuid();
        await SeedAsync(plnInvoice, plnBankTransaction, eurInvoice, eurBankTransaction);

        Assert.True((await ConfirmAsync(
            organizationId,
            plnInvoice.Id,
            plnBankTransaction.Id,
            400m,
            idempotencyKey)).IsSuccess);
        Result<ConfirmExplicitAmountResponse> second = await ConfirmAsync(
            organizationId,
            eurInvoice.Id,
            eurBankTransaction.Id,
            new Money(400m, Eur),
            idempotencyKey);

        Assert.Equal(
            ReconciliationApplicationErrors.IdempotencyPayloadConflict.Code,
            second.Error?.Code);

        await using ReconciliationDbContext reload = _fixture.CreateContext();
        Invoice storedEur = await reload.Invoices.SingleAsync(candidate => candidate.Id == eurInvoice.Id);
        Assert.Equal(Money.Zero(Eur), storedEur.AllocatedAmount);
    }

    [Fact]
    public async Task Same_key_with_a_different_pair_does_not_write_again()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice firstInvoice = PersistenceTestData.CreateInvoice(organizationId, 300m, "INV-A");
        BankTransaction firstBankTransaction = PersistenceTestData.CreateBankTransaction(
            organizationId,
            300m,
            reference: "A");
        Invoice secondInvoice = PersistenceTestData.CreateInvoice(organizationId, 300m, "INV-B");
        BankTransaction secondBankTransaction = PersistenceTestData.CreateBankTransaction(
            organizationId,
            300m,
            reference: "B");
        Guid idempotencyKey = Guid.NewGuid();
        await SeedAsync(firstInvoice, firstBankTransaction, secondInvoice, secondBankTransaction);

        Assert.True((await ConfirmAsync(
            organizationId,
            firstInvoice.Id,
            firstBankTransaction.Id,
            300m,
            idempotencyKey)).IsSuccess);
        Result<ConfirmExplicitAmountResponse> second = await ConfirmAsync(
            organizationId,
            secondInvoice.Id,
            secondBankTransaction.Id,
            300m,
            idempotencyKey);

        Assert.Equal(
            ReconciliationApplicationErrors.IdempotencyPayloadConflict.Code,
            second.Error?.Code);

        await using ReconciliationDbContext reload = _fixture.CreateContext();
        Invoice storedSecond = await reload.Invoices.SingleAsync(candidate => candidate.Id == secondInvoice.Id);
        Assert.Equal(Money.Zero(PersistenceTestData.Pln), storedSecond.AllocatedAmount);
    }

    [Fact]
    public async Task The_same_key_can_allocate_in_a_different_organization()
    {
        Guid idempotencyKey = Guid.NewGuid();
        OrganizationId firstOrganization = OrganizationId.New();
        OrganizationId secondOrganization = OrganizationId.New();
        Invoice firstInvoice = PersistenceTestData.CreateInvoice(firstOrganization, 40m);
        BankTransaction firstBankTransaction = PersistenceTestData.CreateBankTransaction(firstOrganization, 40m);
        Invoice secondInvoice = PersistenceTestData.CreateInvoice(secondOrganization, 40m);
        BankTransaction secondBankTransaction = PersistenceTestData.CreateBankTransaction(secondOrganization, 40m);
        await SeedAsync(firstInvoice, firstBankTransaction, secondInvoice, secondBankTransaction);

        Result<ConfirmExplicitAmountResponse> first = await ConfirmAsync(
            firstOrganization,
            firstInvoice.Id,
            firstBankTransaction.Id,
            40m,
            idempotencyKey);
        Result<ConfirmExplicitAmountResponse> second = await ConfirmAsync(
            secondOrganization,
            secondInvoice.Id,
            secondBankTransaction.Id,
            40m,
            idempotencyKey);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.NotNull(first.Value);
        Assert.NotNull(second.Value);
        Assert.NotEqual(first.Value.AllocationId, second.Value.AllocationId);
    }

    [Fact]
    public async Task An_exact_match_key_cannot_be_replayed_as_an_explicit_amount()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 1000m, "INV-001");
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(
            organizationId,
            1000m,
            reference: "INV-001");
        Guid idempotencyKey = Guid.NewGuid();
        await SeedAsync(invoice, bankTransaction);
        Assert.True((await ConfirmExactAsync(
            organizationId,
            invoice.Id,
            bankTransaction.Id,
            idempotencyKey)).IsSuccess);

        Invoice remainderInvoice = PersistenceTestData.CreateInvoice(organizationId, 100m, "INV-002");
        BankTransaction remainderBankTransaction = PersistenceTestData.CreateBankTransaction(
            organizationId,
            100m,
            reference: "INV-002");
        await SeedAsync(remainderInvoice, remainderBankTransaction);

        Result<ConfirmExplicitAmountResponse> explicitAmount = await ConfirmAsync(
            organizationId,
            remainderInvoice.Id,
            remainderBankTransaction.Id,
            100m,
            idempotencyKey);

        Assert.Equal(
            ReconciliationApplicationErrors.IdempotencyPayloadConflict.Code,
            explicitAmount.Error?.Code);

        await using ReconciliationDbContext reload = _fixture.CreateContext();
        Invoice storedRemainder = await reload.Invoices.SingleAsync(
            candidate => candidate.Id == remainderInvoice.Id);
        Assert.Equal(Money.Zero(PersistenceTestData.Pln), storedRemainder.AllocatedAmount);
        Allocation stored = await reload.Allocations.SingleAsync(
            allocation => allocation.IdempotencyKey == idempotencyKey);
        Assert.Equal(AllocationIdempotencyOperation.ExactMatch, stored.IdempotencyOperation);
    }

    [Fact]
    public async Task An_explicit_amount_key_cannot_confirm_an_exact_match()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 1000m, "INV-001");
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(
            organizationId,
            1000m,
            reference: "INV-001");
        Guid idempotencyKey = Guid.NewGuid();
        await SeedAsync(invoice, bankTransaction);

        Assert.True((await ConfirmAsync(
            organizationId,
            invoice.Id,
            bankTransaction.Id,
            400m,
            idempotencyKey)).IsSuccess);
        Result<ConfirmExactMatchResponse> exact = await ConfirmExactAsync(
            organizationId,
            invoice.Id,
            bankTransaction.Id,
            idempotencyKey);

        Assert.Equal(
            ReconciliationApplicationErrors.IdempotencyPayloadConflict.Code,
            exact.Error?.Code);

        await using ReconciliationDbContext reload = _fixture.CreateContext();
        Invoice storedInvoice = await reload.Invoices.SingleAsync(candidate => candidate.Id == invoice.Id);
        Assert.Equal(new Money(400m, PersistenceTestData.Pln), storedInvoice.AllocatedAmount);
        Assert.Equal(
            1,
            await reload.Allocations.CountAsync(allocation => allocation.OrganizationId == organizationId));
    }

    [Fact]
    public async Task Validation_failure_then_save_persists_nothing()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 1000m);
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(organizationId, 200m);
        await SeedAsync(invoice, bankTransaction);

        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        ConfirmExplicitAmountHandler handler = CreateHandler(scope.ServiceProvider, organizationId);
        Result<ConfirmExplicitAmountResponse> result = await handler.Handle(
            new ConfirmExplicitAmountCommand(
                invoice.Id,
                bankTransaction.Id,
                new Money(400m, PersistenceTestData.Pln),
                Guid.NewGuid()),
            CancellationToken.None);

        Assert.Equal(
            ReconciliationErrors.BankTransactionAllocationExceedsUnapplied.Code,
            result.Error?.Code);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>()
            .SaveChangesAsync(CancellationToken.None);

        await AssertUnchangedAsync(organizationId, invoice.Id, bankTransaction.Id);
    }

    [Fact]
    public async Task Currency_mismatch_then_save_persists_nothing()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 1000m);
        BankTransaction bankTransaction = new(
            BankTransactionId.New(),
            organizationId,
            new Money(1000m, Eur),
            BookingDate,
            "INV-001");
        await SeedAsync(invoice, bankTransaction);

        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        ConfirmExplicitAmountHandler handler = CreateHandler(scope.ServiceProvider, organizationId);
        Result<ConfirmExplicitAmountResponse> result = await handler.Handle(
            new ConfirmExplicitAmountCommand(
                invoice.Id,
                bankTransaction.Id,
                new Money(100m, PersistenceTestData.Pln),
                Guid.NewGuid()),
            CancellationToken.None);

        Assert.Equal(ReconciliationErrors.CurrencyMismatch.Code, result.Error?.Code);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>()
            .SaveChangesAsync(CancellationToken.None);

        await using ReconciliationDbContext reload = _fixture.CreateContext();
        Assert.Equal(
            0,
            await reload.Allocations.CountAsync(allocation => allocation.OrganizationId == organizationId));
        Invoice storedInvoice = await reload.Invoices.SingleAsync(candidate => candidate.Id == invoice.Id);
        BankTransaction storedBankTransaction = await reload.BankTransactions.SingleAsync(
            candidate => candidate.Id == bankTransaction.Id);
        Assert.Equal(Money.Zero(PersistenceTestData.Pln), storedInvoice.AllocatedAmount);
        Assert.Equal(Money.Zero(Eur), storedBankTransaction.AllocatedAmount);
    }

    [Fact]
    public async Task Failed_allocation_insert_rolls_back_the_balance_updates_and_the_key()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 1000m);
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(organizationId, 1000m);
        await SeedAsync(invoice, bankTransaction);

        await using ReconciliationDbContext context = _fixture.CreateContext();
        await context.Database.ExecuteSqlRawAsync(
            """
            CREATE TRIGGER [reconciliation].[TR_Allocations_FailExplicitAmount]
            ON [reconciliation].[Allocations]
            AFTER INSERT
            AS
            BEGIN
                THROW 50001, 'FinOpsDesk integration test forced failure', 1;
            END
            """);

        try
        {
            await using ServiceProvider provider = _fixture.CreateServiceProvider();
            await using AsyncServiceScope scope = provider.CreateAsyncScope();
            ConfirmExplicitAmountHandler handler = CreateHandler(scope.ServiceProvider, organizationId);

            await Assert.ThrowsAsync<DbUpdateException>(
                () => handler.Handle(
                    new ConfirmExplicitAmountCommand(
                        invoice.Id,
                        bankTransaction.Id,
                        new Money(400m, PersistenceTestData.Pln),
                        Guid.NewGuid()),
                    CancellationToken.None));
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync(
                """
                IF OBJECT_ID(N'[reconciliation].[TR_Allocations_FailExplicitAmount]', N'TR') IS NOT NULL
                    DROP TRIGGER [reconciliation].[TR_Allocations_FailExplicitAmount];
                """);
        }

        await AssertUnchangedAsync(organizationId, invoice.Id, bankTransaction.Id);
    }

    [Fact]
    public async Task Keyless_internal_allocation_keeps_a_null_operation()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 500m);
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(organizationId, 500m);
        await SeedAsync(invoice, bankTransaction);

        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        var handler = new ConfirmAllocationHandler(
            new TestCurrentOrganizationContext(organizationId),
            scope.ServiceProvider.GetRequiredService<IInvoiceRepository>(),
            scope.ServiceProvider.GetRequiredService<IBankTransactionRepository>(),
            scope.ServiceProvider.GetRequiredService<IAllocationRepository>(),
            scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
            new FixedTimeProvider(ConfirmedAt));

        Result<ConfirmAllocationResult> result = await handler.Handle(
            new ConfirmAllocationCommand(
                invoice.Id,
                bankTransaction.Id,
                new Money(125m, PersistenceTestData.Pln)),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        await using ReconciliationDbContext reload = _fixture.CreateContext();
        Allocation stored = await reload.Allocations.SingleAsync(
            allocation => allocation.OrganizationId == organizationId);
        Assert.Null(stored.IdempotencyKey);
        Assert.Null(stored.IdempotencyOperation);
        Assert.Equal(new Money(125m, PersistenceTestData.Pln), stored.Amount);
    }

    [Fact]
    public async Task Check_constraint_rejects_a_key_without_an_operation_and_an_operation_without_a_key()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 80m);
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(organizationId, 80m);
        Guid idempotencyKey = Guid.NewGuid();
        await SeedAsync(invoice, bankTransaction);
        Result<ConfirmExplicitAmountResponse> created = await ConfirmAsync(
            organizationId,
            invoice.Id,
            bankTransaction.Id,
            80m,
            idempotencyKey);
        Assert.NotNull(created.Value);

        await using ReconciliationDbContext context = _fixture.CreateContext();
        SqlException missingOperation = await Assert.ThrowsAsync<SqlException>(
            () => context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE [reconciliation].[Allocations]
                SET [IdempotencyOperation] = NULL
                WHERE [Id] = {created.Value.AllocationId};
                """));
        SqlException missingKey = await Assert.ThrowsAsync<SqlException>(
            () => context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE [reconciliation].[Allocations]
                SET [IdempotencyKey] = NULL
                WHERE [Id] = {created.Value.AllocationId};
                """));
        SqlException undefinedOperation = await Assert.ThrowsAsync<SqlException>(
            () => context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE [reconciliation].[Allocations]
                SET [IdempotencyOperation] = 9
                WHERE [Id] = {created.Value.AllocationId};
                """));

        Assert.Equal(547, missingOperation.Number);
        Assert.Equal(547, missingKey.Number);
        Assert.Equal(547, undefinedOperation.Number);
    }

    [Fact(Timeout = 60000)]
    public async Task Concurrent_requests_with_the_same_key_persist_one_allocation()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 1000m);
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(organizationId, 1000m);
        Guid idempotencyKey = Guid.NewGuid();
        await SeedAsync(invoice, bankTransaction);

        Result<ConfirmExplicitAmountResponse>[] results = await ConfirmTogetherAsync(
            organizationId,
            (invoice.Id, bankTransaction.Id, 400m, idempotencyKey),
            (invoice.Id, bankTransaction.Id, 400m, idempotencyKey));

        Assert.All(results, result => Assert.True(result.IsSuccess, result.Error?.Code));
        Assert.Single(results, result => result.Value is { Replayed: false });
        Assert.Single(results, result => result.Value is { Replayed: true });
        Assert.Equal(results[0].Value!.AllocationId, results[1].Value!.AllocationId);

        await using ReconciliationDbContext reload = _fixture.CreateContext();
        Allocation stored = await reload.Allocations.SingleAsync(
            allocation => allocation.OrganizationId == organizationId);
        Invoice storedInvoice = await reload.Invoices.SingleAsync(candidate => candidate.Id == invoice.Id);
        Assert.Equal(AllocationIdempotencyOperation.ExplicitAmount, stored.IdempotencyOperation);
        Assert.Equal(new Money(400m, PersistenceTestData.Pln), storedInvoice.AllocatedAmount);
    }

    [Fact(Timeout = 60000)]
    public async Task Concurrent_independent_pairs_with_the_same_key_conflict_on_the_unique_index()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoiceA = PersistenceTestData.CreateInvoice(organizationId, 500m, "INV-A");
        Invoice invoiceB = PersistenceTestData.CreateInvoice(organizationId, 700m, "INV-B");
        BankTransaction bankA = PersistenceTestData.CreateBankTransaction(
            organizationId,
            500m,
            reference: "PAY-A");
        BankTransaction bankB = PersistenceTestData.CreateBankTransaction(
            organizationId,
            700m,
            reference: "PAY-B");
        Guid idempotencyKey = Guid.NewGuid();
        await SeedAsync(invoiceA, invoiceB, bankA, bankB);

        Result<ConfirmExplicitAmountResponse>[] results = await ConfirmTogetherAsync(
            organizationId,
            (invoiceA.Id, bankA.Id, 500m, idempotencyKey),
            (invoiceB.Id, bankB.Id, 700m, idempotencyKey));

        Result<ConfirmExplicitAmountResponse> winner = Assert.Single(results, result => result.IsSuccess);
        Result<ConfirmExplicitAmountResponse> loser = Assert.Single(results, result => result.IsFailure);
        Assert.NotNull(winner.Value);
        Assert.False(winner.Value.Replayed);
        Assert.Equal(
            ReconciliationApplicationErrors.IdempotencyPayloadConflict.Code,
            loser.Error?.Code);

        bool firstPairWon = winner.Value.InvoiceId == invoiceA.Id.Value;
        InvoiceId winningInvoiceId = firstPairWon ? invoiceA.Id : invoiceB.Id;
        BankTransactionId winningBankTransactionId = firstPairWon ? bankA.Id : bankB.Id;
        InvoiceId losingInvoiceId = firstPairWon ? invoiceB.Id : invoiceA.Id;
        BankTransactionId losingBankTransactionId = firstPairWon ? bankB.Id : bankA.Id;
        decimal winningAmount = firstPairWon ? 500m : 700m;

        await using ReconciliationDbContext reload = _fixture.CreateContext();
        Allocation stored = await reload.Allocations.SingleAsync(
            allocation => allocation.OrganizationId == organizationId);
        Assert.Equal(winningInvoiceId, stored.InvoiceId);
        Assert.Equal(winningBankTransactionId, stored.BankTransactionId);
        Assert.Equal(idempotencyKey, stored.IdempotencyKey);
        Assert.Equal(AllocationIdempotencyOperation.ExplicitAmount, stored.IdempotencyOperation);
        Assert.Equal(new Money(winningAmount, PersistenceTestData.Pln), stored.Amount);

        Invoice winningInvoice = await reload.Invoices.SingleAsync(
            candidate => candidate.Id == winningInvoiceId);
        BankTransaction winningBank = await reload.BankTransactions.SingleAsync(
            candidate => candidate.Id == winningBankTransactionId);
        Invoice losingInvoice = await reload.Invoices.SingleAsync(
            candidate => candidate.Id == losingInvoiceId);
        BankTransaction losingBank = await reload.BankTransactions.SingleAsync(
            candidate => candidate.Id == losingBankTransactionId);

        Assert.Equal(new Money(winningAmount, PersistenceTestData.Pln), winningInvoice.AllocatedAmount);
        Assert.Equal(Money.Zero(PersistenceTestData.Pln), winningInvoice.OutstandingAmount);
        Assert.Equal(new Money(winningAmount, PersistenceTestData.Pln), winningBank.AllocatedAmount);
        Assert.Equal(Money.Zero(PersistenceTestData.Pln), winningBank.UnappliedAmount);
        Assert.Equal(Money.Zero(PersistenceTestData.Pln), losingInvoice.AllocatedAmount);
        Assert.Equal(Money.Zero(PersistenceTestData.Pln), losingBank.AllocatedAmount);
    }

    [Fact(Timeout = 60000)]
    public async Task Concurrent_requests_with_different_keys_persist_one_shared_invoice_update()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 1000m);
        BankTransaction first = PersistenceTestData.CreateBankTransaction(organizationId, 1000m, reference: "A");
        BankTransaction second = PersistenceTestData.CreateBankTransaction(organizationId, 1000m, reference: "B");
        await SeedAsync(invoice, first, second);

        Result<ConfirmExplicitAmountResponse>[] results = await ConfirmTogetherAsync(
            organizationId,
            (invoice.Id, first.Id, 400m, Guid.NewGuid()),
            (invoice.Id, second.Id, 400m, Guid.NewGuid()));

        Assert.Single(results, result => result.IsSuccess);
        Assert.Single(
            results,
            result => result.IsFailure
                && result.Error?.Code == ReconciliationApplicationErrors.ConcurrencyConflict.Code);

        await using ReconciliationDbContext reload = _fixture.CreateContext();
        Invoice storedInvoice = await reload.Invoices.SingleAsync(candidate => candidate.Id == invoice.Id);
        Assert.Equal(new Money(400m, PersistenceTestData.Pln), storedInvoice.AllocatedAmount);
        Assert.Equal(
            1,
            await reload.Allocations.CountAsync(allocation => allocation.OrganizationId == organizationId));
    }

    [Fact(Timeout = 60000)]
    public async Task Same_key_committed_before_validation_replays_instead_of_reporting_insufficient_balance()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 400m);
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(organizationId, 400m);
        Guid idempotencyKey = Guid.NewGuid();
        await SeedAsync(invoice, bankTransaction);

        Result<ConfirmExplicitAmountResponse> loser = await ConfirmAfterInitialLookupAsync(
            organizationId,
            invoice.Id,
            bankTransaction.Id,
            400m,
            idempotencyKey,
            async () =>
            {
                Result<ConfirmExplicitAmountResponse> winner = await ConfirmAsync(
                    organizationId,
                    invoice.Id,
                    bankTransaction.Id,
                    400m,
                    idempotencyKey);
                Assert.True(winner.IsSuccess, winner.Error?.Code);
            });

        Assert.True(loser.IsSuccess);
        Assert.NotNull(loser.Value);
        Assert.True(loser.Value.Replayed);
        Assert.Equal(400m, loser.Value.Amount.Amount);

        await using ReconciliationDbContext reload = _fixture.CreateContext();
        Assert.Equal(
            1,
            await reload.Allocations.CountAsync(allocation => allocation.OrganizationId == organizationId));
        Invoice storedInvoice = await reload.Invoices.SingleAsync(candidate => candidate.Id == invoice.Id);
        Assert.Equal(new Money(400m, PersistenceTestData.Pln), storedInvoice.AllocatedAmount);
    }

    [Fact(Timeout = 60000)]
    public async Task Different_operation_committed_before_validation_conflicts_instead_of_reporting_a_balance_error()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 1000m, "INV-001");
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(
            organizationId,
            1000m,
            reference: "INV-001");
        Guid idempotencyKey = Guid.NewGuid();
        await SeedAsync(invoice, bankTransaction);

        Result<ConfirmExplicitAmountResponse> loser = await ConfirmAfterInitialLookupAsync(
            organizationId,
            invoice.Id,
            bankTransaction.Id,
            400m,
            idempotencyKey,
            async () =>
            {
                Result<ConfirmExactMatchResponse> winner = await ConfirmExactAsync(
                    organizationId,
                    invoice.Id,
                    bankTransaction.Id,
                    idempotencyKey);
                Assert.True(winner.IsSuccess, winner.Error?.Code);
            });

        Assert.Equal(
            ReconciliationApplicationErrors.IdempotencyPayloadConflict.Code,
            loser.Error?.Code);

        await using ReconciliationDbContext reload = _fixture.CreateContext();
        Allocation stored = await reload.Allocations.SingleAsync(
            allocation => allocation.OrganizationId == organizationId);
        Assert.Equal(AllocationIdempotencyOperation.ExactMatch, stored.IdempotencyOperation);
        Assert.Equal(new Money(1000m, PersistenceTestData.Pln), stored.Amount);
    }

    [Fact(Timeout = 60000)]
    public async Task Exact_match_replays_when_the_same_key_commits_before_validation()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 1000m, "INV-001");
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(
            organizationId,
            1000m,
            reference: "INV-001");
        Guid idempotencyKey = Guid.NewGuid();
        await SeedAsync(invoice, bankTransaction);

        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        var blocker = new BlockingInvoiceRepository(
            scope.ServiceProvider.GetRequiredService<IInvoiceRepository>());
        var handler = new ConfirmExactMatchHandler(
            new TestCurrentOrganizationContext(organizationId),
            new TestCurrentActorContext(),
            blocker,
            scope.ServiceProvider.GetRequiredService<IBankTransactionRepository>(),
            scope.ServiceProvider.GetRequiredService<IAllocationRepository>(),
            scope.ServiceProvider.GetRequiredService<IAllocationIdempotencyLookup>(),
            scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
            scope.ServiceProvider.GetRequiredService<IFinancialAuditWriter>(),
            scope.ServiceProvider.GetRequiredService<IAllocationIdempotencyConflict>(),
            new FixedTimeProvider(ConfirmedAt));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task<Result<ConfirmExactMatchResponse>> loserTask = handler.Handle(
            new ConfirmExactMatchCommand(invoice.Id, bankTransaction.Id, idempotencyKey),
            timeout.Token);

        Result<ConfirmExactMatchResponse> winner;
        try
        {
            await blocker.ReachedLoad.WaitAsync(timeout.Token);
            winner = await ConfirmExactAsync(
                organizationId,
                invoice.Id,
                bankTransaction.Id,
                idempotencyKey);
        }
        finally
        {
            blocker.Release();
        }

        Result<ConfirmExactMatchResponse> loser = await loserTask.WaitAsync(timeout.Token);

        Assert.True(winner.IsSuccess);
        Assert.True(loser.IsSuccess);
        Assert.NotNull(winner.Value);
        Assert.NotNull(loser.Value);
        Assert.False(winner.Value.Replayed);
        Assert.True(loser.Value.Replayed);
        Assert.Equal(winner.Value.AllocationId, loser.Value.AllocationId);

        await using ReconciliationDbContext reload = _fixture.CreateContext();
        Assert.Equal(
            1,
            await reload.Allocations.CountAsync(allocation => allocation.OrganizationId == organizationId));
    }

    private async Task<Result<ConfirmExplicitAmountResponse>> ConfirmAsync(
        OrganizationId organizationId,
        InvoiceId invoiceId,
        BankTransactionId bankTransactionId,
        decimal amount,
        Guid idempotencyKey)
    {
        return await ConfirmAsync(
            organizationId,
            invoiceId,
            bankTransactionId,
            new Money(amount, PersistenceTestData.Pln),
            idempotencyKey);
    }

    private async Task<Result<ConfirmExplicitAmountResponse>> ConfirmAsync(
        OrganizationId organizationId,
        InvoiceId invoiceId,
        BankTransactionId bankTransactionId,
        Money amount,
        Guid idempotencyKey)
    {
        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        ConfirmExplicitAmountHandler handler = CreateHandler(scope.ServiceProvider, organizationId);
        return await handler.Handle(
            new ConfirmExplicitAmountCommand(
                invoiceId,
                bankTransactionId,
                amount,
                idempotencyKey),
            CancellationToken.None);
    }

    private async Task<Result<ConfirmExactMatchResponse>> ConfirmExactAsync(
        OrganizationId organizationId,
        InvoiceId invoiceId,
        BankTransactionId bankTransactionId,
        Guid idempotencyKey)
    {
        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        var handler = new ConfirmExactMatchHandler(
            new TestCurrentOrganizationContext(organizationId),
            new TestCurrentActorContext(),
            scope.ServiceProvider.GetRequiredService<IInvoiceRepository>(),
            scope.ServiceProvider.GetRequiredService<IBankTransactionRepository>(),
            scope.ServiceProvider.GetRequiredService<IAllocationRepository>(),
            scope.ServiceProvider.GetRequiredService<IAllocationIdempotencyLookup>(),
            scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
            scope.ServiceProvider.GetRequiredService<IFinancialAuditWriter>(),
            scope.ServiceProvider.GetRequiredService<IAllocationIdempotencyConflict>(),
            new FixedTimeProvider(ConfirmedAt));
        return await handler.Handle(
            new ConfirmExactMatchCommand(invoiceId, bankTransactionId, idempotencyKey),
            CancellationToken.None);
    }

    private async Task<Result<ConfirmExplicitAmountResponse>> ConfirmAfterInitialLookupAsync(
        OrganizationId organizationId,
        InvoiceId invoiceId,
        BankTransactionId bankTransactionId,
        decimal amount,
        Guid idempotencyKey,
        Func<Task> commitWinner)
    {
        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        var blocker = new BlockingInvoiceRepository(
            scope.ServiceProvider.GetRequiredService<IInvoiceRepository>());
        ConfirmExplicitAmountHandler handler = CreateHandler(
            scope.ServiceProvider,
            organizationId,
            blocker);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task<Result<ConfirmExplicitAmountResponse>> loser = handler.Handle(
            new ConfirmExplicitAmountCommand(
                invoiceId,
                bankTransactionId,
                new Money(amount, PersistenceTestData.Pln),
                idempotencyKey),
            timeout.Token);

        try
        {
            await blocker.ReachedLoad.WaitAsync(timeout.Token);
            await commitWinner();
        }
        finally
        {
            blocker.Release();
        }

        return await loser.WaitAsync(timeout.Token);
    }

    private async Task<Result<ConfirmExplicitAmountResponse>[]> ConfirmTogetherAsync(
        OrganizationId organizationId,
        params (InvoiceId InvoiceId, BankTransactionId BankTransactionId, decimal Amount, Guid IdempotencyKey)[] requests)
    {
        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        using var gate = new Barrier(requests.Length);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        Task<Result<ConfirmExplicitAmountResponse>> Start(
            (InvoiceId InvoiceId, BankTransactionId BankTransactionId, decimal Amount, Guid IdempotencyKey) request)
        {
            return Task.Run(
                async () =>
                {
                    await using AsyncServiceScope scope = provider.CreateAsyncScope();
                    ConfirmExplicitAmountHandler handler = CreateHandler(
                        scope.ServiceProvider,
                        organizationId,
                        unitOfWork: new GatedUnitOfWork(
                            scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
                            gate));
                    return await handler.Handle(
                        new ConfirmExplicitAmountCommand(
                            request.InvoiceId,
                            request.BankTransactionId,
                            new Money(request.Amount, PersistenceTestData.Pln),
                            request.IdempotencyKey),
                        timeout.Token);
                },
                timeout.Token);
        }

        return await Task.WhenAll(requests.Select(Start));
    }

    private static ConfirmExplicitAmountHandler CreateHandler(
        IServiceProvider services,
        OrganizationId organizationId,
        IInvoiceRepository? invoiceRepository = null,
        IUnitOfWork? unitOfWork = null)
    {
        return new ConfirmExplicitAmountHandler(
            new TestCurrentOrganizationContext(organizationId),
            new TestCurrentActorContext(),
            invoiceRepository ?? services.GetRequiredService<IInvoiceRepository>(),
            services.GetRequiredService<IBankTransactionRepository>(),
            services.GetRequiredService<IAllocationRepository>(),
            services.GetRequiredService<IAllocationIdempotencyLookup>(),
            unitOfWork ?? services.GetRequiredService<IUnitOfWork>(),
            services.GetRequiredService<IFinancialAuditWriter>(),
            services.GetRequiredService<IAllocationIdempotencyConflict>(),
            new FixedTimeProvider(ConfirmedAt));
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
        Assert.Equal(Money.Zero(bankTransaction.Amount.Currency), bankTransaction.AllocatedAmount);
        Assert.Equal(
            0,
            await reload.Allocations.CountAsync(allocation => allocation.OrganizationId == organizationId));
    }

    private async Task<byte[]> RowVersionAsync(InvoiceId invoiceId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Invoice invoice = await context.Invoices.SingleAsync(candidate => candidate.Id == invoiceId);
        return context.Entry(invoice).Property<byte[]>("RowVersion").CurrentValue.ToArray();
    }

    private sealed class GatedUnitOfWork : IUnitOfWork
    {
        private readonly IUnitOfWork _inner;
        private readonly Barrier _barrier;

        public GatedUnitOfWork(IUnitOfWork inner, Barrier barrier)
        {
            _inner = inner;
            _barrier = barrier;
        }

        public async Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            _barrier.SignalAndWait(cancellationToken);
            await _inner.SaveChangesAsync(cancellationToken);
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return _utcNow;
        }
    }
}