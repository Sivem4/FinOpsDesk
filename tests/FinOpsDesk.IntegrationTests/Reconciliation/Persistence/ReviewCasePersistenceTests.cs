using System.Data;

using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace FinOpsDesk.IntegrationTests.Reconciliation.Persistence;

[Collection(SqlServerCollection.Name)]
public sealed class ReviewCasePersistenceTests
{
    private readonly SqlServerFixture _fixture;

    public ReviewCasePersistenceTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Review_case_round_trips_and_stores_the_full_competing_subject_key()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 100m);
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(organizationId, 100m);
        var openedAt = new DateTimeOffset(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);
        var closedAt = new DateTimeOffset(2026, 10, 26, 11, 0, 0, TimeSpan.Zero);
        ReviewCase reviewCase = ReviewCase.Open(
            ReviewCaseId.New(),
            organizationId,
            ReviewCaseReason.CompetingMatch,
            invoice.Id,
            bankTransaction.Id,
            ReviewCaseSource.Smart1,
            "  Needs a person  ",
            openedAt);
        AllocationId allocationId = AllocationId.New();
        Assert.Equal(ReviewCaseDecision.Applied, reviewCase.Resolve(
            ReviewCaseOutcome.AllocationRecorded,
            allocationId,
            "  Already allocated  ",
            closedAt).Value);

        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            context.Invoices.Add(invoice);
            context.BankTransactions.Add(bankTransaction);
            context.ReviewCases.Add(reviewCase);
            await context.SaveChangesAsync();

            byte[] insertedRowVersion = RowVersion(context, reviewCase);
            Assert.NotEmpty(insertedRowVersion);
        }

        await using ReconciliationDbContext reload = _fixture.CreateContext();
        ReviewCase loaded = await reload.ReviewCases.SingleAsync(stored => stored.Id == reviewCase.Id);

        Assert.Equal(reviewCase.SubjectKey, loaded.SubjectKey);
        Assert.Equal(83, loaded.SubjectKey.Length);
        Assert.Equal(96, await SubjectKeyMaxLengthAsync(reload));
        Assert.Equal(ReviewCaseStatus.Resolved, loaded.Status);
        Assert.Equal(ReviewCaseOutcome.AllocationRecorded, loaded.Outcome);
        Assert.Equal(allocationId, loaded.AllocationId);
        Assert.Equal(ReviewCaseSource.Smart1, loaded.SourceLabel);
        Assert.Equal("Needs a person", loaded.Note);
        Assert.Equal("Already allocated", loaded.ResolutionNote);
        Assert.Equal(openedAt, loaded.OpenedAt);
        Assert.Equal(closedAt, loaded.ClosedAt);
        await AssertFinancialStateUnchangedAsync(organizationId, invoice.Id, bankTransaction.Id);
    }

    [Fact]
    public async Task Second_open_case_for_the_same_subject_violates_the_filtered_unique_index()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 40m);
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(organizationId, 40m);
        await SeedAsync(invoice, bankTransaction);

        ReviewCase first = OpenCompeting(organizationId, invoice.Id, bankTransaction.Id, note: null);
        ReviewCase second = OpenCompeting(organizationId, invoice.Id, bankTransaction.Id, note: "other");
        await using ReconciliationDbContext firstContext = _fixture.CreateContext();
        await using ReconciliationDbContext secondContext = _fixture.CreateContext();
        firstContext.ReviewCases.Add(first);
        secondContext.ReviewCases.Add(second);
        await firstContext.SaveChangesAsync();

        DbUpdateException exception = await Assert.ThrowsAsync<DbUpdateException>(
            () => secondContext.SaveChangesAsync());

        Assert.True(IsUniqueViolation(exception));
        await using ReconciliationDbContext reload = _fixture.CreateContext();
        ReviewCase stored = await reload.ReviewCases.SingleAsync(
            storedCase => storedCase.OrganizationId == organizationId);
        Assert.Equal(first.Id, stored.Id);
        Assert.Equal(ReviewCaseStatus.Open, stored.Status);
        Assert.Null(stored.Note);
    }

    [Fact]
    public async Task Terminal_case_allows_a_new_open_case_for_the_same_subject()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 55m);
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(organizationId, 55m);
        ReviewCase resolved = OpenCompeting(organizationId, invoice.Id, bankTransaction.Id, note: null);
        Assert.Equal(ReviewCaseDecision.Applied, resolved.Resolve(
            ReviewCaseOutcome.NoActionRequired,
            allocationId: null,
            resolutionNote: null,
            resolved.OpenedAt.AddHours(1)).Value);
        ReviewCase reopened = OpenCompeting(organizationId, invoice.Id, bankTransaction.Id, note: "again");

        await using ReconciliationDbContext context = _fixture.CreateContext();
        context.Invoices.Add(invoice);
        context.BankTransactions.Add(bankTransaction);
        context.ReviewCases.Add(resolved);
        context.ReviewCases.Add(reopened);
        await context.SaveChangesAsync();

        int stored = await context.ReviewCases.CountAsync(
            storedCase => storedCase.OrganizationId == organizationId
                && storedCase.SubjectKey == resolved.SubjectKey);
        Assert.Equal(2, stored);
        Assert.Equal(resolved.SubjectKey, reopened.SubjectKey);
        Assert.NotEqual(resolved.Id, reopened.Id);
    }

    [Fact]
    public async Task Different_subjects_sharing_one_invoice_can_both_be_open()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 70m);
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(organizationId, 70m);
        var openedAt = new DateTimeOffset(2026, 10, 26, 8, 0, 0, TimeSpan.Zero);
        ReviewCase competing = OpenCompeting(organizationId, invoice.Id, bankTransaction.Id, note: null);
        ReviewCase investigation = ReviewCase.Open(
            ReviewCaseId.New(),
            organizationId,
            ReviewCaseReason.NeedsInvestigation,
            invoice.Id,
            bankTransactionId: null,
            sourceLabel: null,
            note: null,
            openedAt);

        await using ReconciliationDbContext context = _fixture.CreateContext();
        context.Invoices.Add(invoice);
        context.BankTransactions.Add(bankTransaction);
        context.ReviewCases.AddRange(competing, investigation);
        await context.SaveChangesAsync();

        Assert.NotEqual(competing.SubjectKey, investigation.SubjectKey);
        Assert.Equal(2, await context.ReviewCases.CountAsync(
            stored => stored.OrganizationId == organizationId && stored.Status == ReviewCaseStatus.Open));
    }

    [Fact]
    public async Task Composite_foreign_keys_reject_cross_tenant_references_and_do_not_cascade()
    {
        OrganizationId owner = OrganizationId.New();
        OrganizationId other = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(owner, 20m);
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(owner, 20m);
        Invoice otherInvoice = PersistenceTestData.CreateInvoice(other, 20m);
        await SeedAsync(invoice, bankTransaction, otherInvoice);

        ReviewCase crossTenant = OpenCompeting(owner, otherInvoice.Id, bankTransaction.Id, note: null);
        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            context.ReviewCases.Add(crossTenant);
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        }

        ReviewCase reviewCase = OpenCompeting(owner, invoice.Id, bankTransaction.Id, note: null);
        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            context.ReviewCases.Add(reviewCase);
            await context.SaveChangesAsync();
        }

        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            await Assert.ThrowsAsync<SqlException>(
                () => context.Database.ExecuteSqlInterpolatedAsync(
                    $"DELETE FROM [reconciliation].[Invoices] WHERE [Id] = {invoice.Id.Value}"));
        }

        await using ReconciliationDbContext reload = _fixture.CreateContext();
        Assert.Equal(1, await reload.ReviewCases.CountAsync(stored => stored.Id == reviewCase.Id));
        Assert.Equal(1, await reload.Invoices.CountAsync(stored => stored.Id == invoice.Id));
        IReadOnlyList<int> deleteActions = await DeleteActionsAsync(reload);
        Assert.Equal(2, deleteActions.Count);
        Assert.All(deleteActions, action => Assert.Equal(0, action));

        reload.ReviewCases.Remove(await reload.ReviewCases.SingleAsync(stored => stored.Id == reviewCase.Id));
        await reload.SaveChangesAsync();
        Assert.Equal(1, await reload.Invoices.CountAsync(stored => stored.Id == invoice.Id));
        Assert.Equal(1, await reload.BankTransactions.CountAsync(stored => stored.Id == bankTransaction.Id));
    }

    [Fact]
    public async Task Stale_rowversion_cannot_overwrite_a_committed_transition()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 30m);
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(organizationId, 30m);
        ReviewCase reviewCase = OpenCompeting(organizationId, invoice.Id, bankTransaction.Id, note: null);
        await using (ReconciliationDbContext seed = _fixture.CreateContext())
        {
            seed.Invoices.Add(invoice);
            seed.BankTransactions.Add(bankTransaction);
            seed.ReviewCases.Add(reviewCase);
            await seed.SaveChangesAsync();
        }

        await using ReconciliationDbContext winnerContext = _fixture.CreateContext();
        await using ReconciliationDbContext loserContext = _fixture.CreateContext();
        ReviewCase winner = await winnerContext.ReviewCases.SingleAsync(stored => stored.Id == reviewCase.Id);
        ReviewCase loser = await loserContext.ReviewCases.SingleAsync(stored => stored.Id == reviewCase.Id);
        byte[] inserted = RowVersion(winnerContext, winner).ToArray();
        DateTimeOffset closedAt = winner.OpenedAt.AddHours(1);
        Assert.Equal(ReviewCaseDecision.Applied, winner.Resolve(
            ReviewCaseOutcome.NoActionRequired,
            allocationId: null,
            "Verified with bank",
            closedAt).Value);
        await winnerContext.SaveChangesAsync();
        byte[] updated = RowVersion(winnerContext, winner).ToArray();
        Assert.False(inserted.SequenceEqual(updated));

        Assert.Equal(ReviewCaseDecision.Applied, loser.Dismiss("Different decision", closedAt).Value);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
            () => loserContext.SaveChangesAsync());

        await using ReconciliationDbContext reload = _fixture.CreateContext();
        ReviewCase stored = await reload.ReviewCases.SingleAsync(candidate => candidate.Id == reviewCase.Id);
        Assert.Equal(ReviewCaseStatus.Resolved, stored.Status);
        Assert.Equal(ReviewCaseOutcome.NoActionRequired, stored.Outcome);
        Assert.Equal("Verified with bank", stored.ResolutionNote);
        Assert.Equal(closedAt, stored.ClosedAt);
        Assert.False(inserted.SequenceEqual(RowVersion(reload, stored)));
    }

    [Fact]
    public async Task Identical_terminal_replay_does_not_change_closed_at_or_rowversion()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 15m);
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(organizationId, 15m);
        ReviewCase reviewCase = OpenCompeting(organizationId, invoice.Id, bankTransaction.Id, note: null);
        DateTimeOffset closedAt = reviewCase.OpenedAt.AddMinutes(30);
        reviewCase.Dismiss("Verified with bank", closedAt);
        await using (ReconciliationDbContext seed = _fixture.CreateContext())
        {
            seed.Invoices.Add(invoice);
            seed.BankTransactions.Add(bankTransaction);
            seed.ReviewCases.Add(reviewCase);
            await seed.SaveChangesAsync();
        }

        await using ReconciliationDbContext context = _fixture.CreateContext();
        ReviewCase loaded = await context.ReviewCases.SingleAsync(stored => stored.Id == reviewCase.Id);
        byte[] version = RowVersion(context, loaded).ToArray();
        var replay = loaded.Dismiss("  Verified with bank  ", closedAt.AddHours(5));
        await context.SaveChangesAsync();

        Assert.Equal(ReviewCaseDecision.IdenticalReplay, replay.Value);
        Assert.Equal(closedAt, loaded.ClosedAt);
        Assert.True(version.SequenceEqual(RowVersion(context, loaded)));
    }

    [Fact]
    public async Task Database_status_checks_reject_shapes_the_domain_does_not_create()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 10m);
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(organizationId, 10m);
        await SeedAsync(invoice, bankTransaction);
        ReviewCase valid = OpenCompeting(organizationId, invoice.Id, bankTransaction.Id, note: null);
        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            context.ReviewCases.Add(valid);
            await context.SaveChangesAsync();
        }

        await using ReconciliationDbContext probe = _fixture.CreateContext();
        SqlException invalidStatus = await Assert.ThrowsAsync<SqlException>(
            () => probe.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO [reconciliation].[ReviewCases]
                    ([Id], [OrganizationId], [Reason], [Status], [InvoiceId], [BankTransactionId], [SubjectKey], [OpenedAt])
                VALUES
                    ({Guid.NewGuid()}, {organizationId.Value}, {1}, {9}, {invoice.Id.Value}, {bankTransaction.Id.Value}, {"competing:invalid-status"}, {valid.OpenedAt})
                """));
        SqlException openWithClose = await Assert.ThrowsAsync<SqlException>(
            () => probe.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO [reconciliation].[ReviewCases]
                    ([Id], [OrganizationId], [Reason], [Status], [InvoiceId], [BankTransactionId], [SubjectKey], [OpenedAt], [ClosedAt])
                VALUES
                    ({Guid.NewGuid()}, {organizationId.Value}, {1}, {1}, {invoice.Id.Value}, {bankTransaction.Id.Value}, {"competing:open-with-close"}, {valid.OpenedAt}, {valid.OpenedAt.AddHours(1)})
                """));

        Assert.Equal(547, invalidStatus.Number);
        Assert.Equal(547, openWithClose.Number);
    }

    [Fact]
    public async Task Status_check_requires_an_outcome_on_every_resolved_case()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 12m);
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(organizationId, 12m);
        await SeedAsync(invoice, bankTransaction);
        var openedAt = new DateTimeOffset(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);
        var closedAt = openedAt.AddHours(1);
        Guid allocationId = Guid.NewGuid();

        await using ReconciliationDbContext context = _fixture.CreateContext();
        string definition = await CheckDefinitionAsync(context, "CK_ReviewCases_StatusShape");
        Assert.Contains("[Outcome] IS NOT NULL", definition, StringComparison.Ordinal);

        await ExpectCheckViolationAsync(
            () => InsertRawReviewCaseAsync(
                context, organizationId, invoice.Id, bankTransaction.Id, "resolved-null-outcome", 2, null, null, openedAt, closedAt));
        await ExpectCheckViolationAsync(
            () => InsertRawReviewCaseAsync(
                context, organizationId, invoice.Id, bankTransaction.Id, "resolved-null-outcome-with-allocation", 2, null, allocationId, openedAt, closedAt));
        await ExpectCheckViolationAsync(
            () => InsertRawReviewCaseAsync(
                context, organizationId, invoice.Id, bankTransaction.Id, "resolved-allocation-recorded-without-id", 2, 1, null, openedAt, closedAt));
        await ExpectCheckViolationAsync(
            () => InsertRawReviewCaseAsync(
                context, organizationId, invoice.Id, bankTransaction.Id, "resolved-no-action-with-allocation", 2, 2, allocationId, openedAt, closedAt));

        await InsertRawReviewCaseAsync(
            context, organizationId, invoice.Id, bankTransaction.Id, "resolved-no-action", 2, 2, null, openedAt, closedAt);
        await InsertRawReviewCaseAsync(
            context, organizationId, invoice.Id, bankTransaction.Id, "resolved-allocation-recorded", 2, 1, allocationId, openedAt, closedAt);

        Assert.Equal(2, await context.ReviewCases.CountAsync(
            stored => stored.OrganizationId == organizationId && stored.Status == ReviewCaseStatus.Resolved));
        Assert.Equal(0, await context.Allocations.CountAsync(
            allocation => allocation.OrganizationId == organizationId));
    }

    [Fact]
    public async Task Closed_at_check_rejects_a_close_before_the_open_time()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 18m);
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(organizationId, 18m);
        await SeedAsync(invoice, bankTransaction);
        var openedAt = new DateTimeOffset(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);
        var earlier = openedAt.AddMinutes(-1);

        await using ReconciliationDbContext context = _fixture.CreateContext();
        string definition = await CheckDefinitionAsync(context, "CK_ReviewCases_ClosedAt");
        Assert.Contains("[ClosedAt] IS NULL OR [ClosedAt]>=[OpenedAt]", definition, StringComparison.Ordinal);

        await InsertRawReviewCaseAsync(
            context, organizationId, invoice.Id, bankTransaction.Id, "open-without-close", 1, null, null, openedAt, null);
        await InsertRawReviewCaseAsync(
            context, organizationId, invoice.Id, bankTransaction.Id, "resolved-after-open", 2, 2, null, openedAt, openedAt.AddHours(1));
        await InsertRawReviewCaseAsync(
            context, organizationId, invoice.Id, bankTransaction.Id, "dismissed-at-open", 3, null, null, openedAt, openedAt);

        await ExpectCheckViolationAsync(
            () => InsertRawReviewCaseAsync(
                context, organizationId, invoice.Id, bankTransaction.Id, "resolved-before-open", 2, 2, null, openedAt, earlier));
        await ExpectCheckViolationAsync(
            () => InsertRawReviewCaseAsync(
                context, organizationId, invoice.Id, bankTransaction.Id, "dismissed-before-open", 3, null, null, openedAt, earlier));

        Assert.Equal(1, await context.ReviewCases.CountAsync(
            stored => stored.OrganizationId == organizationId && stored.Status == ReviewCaseStatus.Open && stored.ClosedAt == null));
        Assert.Equal(1, await context.ReviewCases.CountAsync(
            stored => stored.OrganizationId == organizationId && stored.Status == ReviewCaseStatus.Resolved && stored.ClosedAt > stored.OpenedAt));
        Assert.Equal(1, await context.ReviewCases.CountAsync(
            stored => stored.OrganizationId == organizationId && stored.Status == ReviewCaseStatus.Dismissed && stored.ClosedAt == stored.OpenedAt));
    }

    [Fact]
    public async Task Rolled_back_insert_does_not_leave_a_review_case()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 25m);
        BankTransaction bankTransaction = PersistenceTestData.CreateBankTransaction(organizationId, 25m);
        await SeedAsync(invoice, bankTransaction);
        ReviewCase reviewCase = OpenCompeting(organizationId, invoice.Id, bankTransaction.Id, note: null);

        await using (ReconciliationDbContext context = _fixture.CreateContext())
        {
            await using var transaction = await context.Database.BeginTransactionAsync();
            context.ReviewCases.Add(reviewCase);
            await context.SaveChangesAsync();
            await transaction.RollbackAsync();
        }

        await using ReconciliationDbContext reload = _fixture.CreateContext();
        Assert.False(await reload.ReviewCases.AnyAsync(stored => stored.Id == reviewCase.Id));
        await AssertFinancialStateUnchangedAsync(organizationId, invoice.Id, bankTransaction.Id);
    }

    [Fact]
    public async Task Open_subject_index_is_unique_and_filtered_to_open_status()
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        (bool Unique, string? Filter, IReadOnlyList<string> Columns) index = await ReadOpenSubjectIndexAsync(context);

        Assert.True(index.Unique);
        Assert.Contains("Status", index.Filter, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1", index.Filter, StringComparison.Ordinal);
        Assert.Equal(new[] { "OrganizationId", "SubjectKey" }, index.Columns);
    }

    private async Task SeedAsync(params object[] entities)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        context.AddRange(entities);
        await context.SaveChangesAsync();
    }

    private static ReviewCase OpenCompeting(
        OrganizationId organizationId,
        InvoiceId invoiceId,
        BankTransactionId bankTransactionId,
        string? note)
    {
        return ReviewCase.Open(
            ReviewCaseId.New(),
            organizationId,
            ReviewCaseReason.CompetingMatch,
            invoiceId,
            bankTransactionId,
            sourceLabel: null,
            note,
            new DateTimeOffset(2026, 10, 26, 9, 30, 0, TimeSpan.Zero));
    }

    private async Task AssertFinancialStateUnchangedAsync(
        OrganizationId organizationId,
        InvoiceId invoiceId,
        BankTransactionId bankTransactionId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Invoice invoice = await context.Invoices.SingleAsync(stored => stored.Id == invoiceId);
        BankTransaction bankTransaction = await context.BankTransactions.SingleAsync(
            stored => stored.Id == bankTransactionId);

        Assert.Equal(Money.Zero(PersistenceTestData.Pln), invoice.AllocatedAmount);
        Assert.Equal(Money.Zero(PersistenceTestData.Pln), bankTransaction.AllocatedAmount);
        Assert.Equal(0, await context.Allocations.CountAsync(
            allocation => allocation.OrganizationId == organizationId));
    }

    private static byte[] RowVersion(ReconciliationDbContext context, ReviewCase reviewCase)
    {
        byte[]? rowVersion = context.Entry(reviewCase)
            .Property<byte[]>("RowVersion")
            .CurrentValue;
        Assert.NotNull(rowVersion);
        return rowVersion.ToArray();
    }

    private static async Task InsertRawReviewCaseAsync(
        ReconciliationDbContext context,
        OrganizationId organizationId,
        InvoiceId invoiceId,
        BankTransactionId bankTransactionId,
        string subjectKey,
        int status,
        int? outcome,
        Guid? allocationId,
        DateTimeOffset openedAt,
        DateTimeOffset? closedAt)
    {
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO [reconciliation].[ReviewCases]
                ([Id], [OrganizationId], [Reason], [Status], [InvoiceId], [BankTransactionId], [SubjectKey], [Outcome], [AllocationId], [OpenedAt], [ClosedAt])
            VALUES
                ({Guid.NewGuid()}, {organizationId.Value}, {1}, {status}, {invoiceId.Value}, {bankTransactionId.Value}, {subjectKey}, {outcome}, {allocationId}, {openedAt}, {closedAt})
            """);
    }

    private static async Task ExpectCheckViolationAsync(Func<Task> insert)
    {
        SqlException exception = await Assert.ThrowsAsync<SqlException>(insert);
        Assert.Equal(547, exception.Number);
    }

    private static async Task<string> CheckDefinitionAsync(
        ReconciliationDbContext context,
        string constraintName)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT definition
            FROM sys.check_constraints
            WHERE name = @name
              AND parent_object_id = OBJECT_ID(N'[reconciliation].[ReviewCases]')
            """;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@name";
        parameter.Value = constraintName;
        command.Parameters.Add(parameter);
        if (command.Connection!.State != ConnectionState.Open)
        {
            await command.Connection.OpenAsync();
        }

        object? value = await command.ExecuteScalarAsync();
        Assert.IsType<string>(value);
        return (string)value;
    }

    private static bool IsUniqueViolation(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqlException sqlException)
            {
                return sqlException.Number is 2601 or 2627;
            }
        }

        return false;
    }

    private static async Task<int> SubjectKeyMaxLengthAsync(ReconciliationDbContext context)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT CHARACTER_MAXIMUM_LENGTH
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = N'reconciliation'
              AND TABLE_NAME = N'ReviewCases'
              AND COLUMN_NAME = N'SubjectKey'
            """;
        if (command.Connection!.State != ConnectionState.Open)
        {
            await command.Connection.OpenAsync();
        }

        object? value = await command.ExecuteScalarAsync();
        return Convert.ToInt32(value);
    }

    private static async Task<IReadOnlyList<int>> DeleteActionsAsync(ReconciliationDbContext context)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT delete_referential_action
            FROM sys.foreign_keys
            WHERE parent_object_id = OBJECT_ID(N'[reconciliation].[ReviewCases]')
            ORDER BY name
            """;
        if (command.Connection!.State != ConnectionState.Open)
        {
            await command.Connection.OpenAsync();
        }

        var actions = new List<int>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            actions.Add(reader.GetByte(0));
        }

        return actions;
    }

    private static async Task<(bool Unique, string? Filter, IReadOnlyList<string> Columns)> ReadOpenSubjectIndexAsync(
        ReconciliationDbContext context)
    {
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT i.is_unique, i.filter_definition, c.name
            FROM sys.indexes AS i
            INNER JOIN sys.index_columns AS ic
                ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            INNER JOIN sys.columns AS c
                ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.name = N'UX_ReviewCases_OrganizationId_SubjectKey_Open'
            ORDER BY ic.key_ordinal
            """;
        if (command.Connection!.State != ConnectionState.Open)
        {
            await command.Connection.OpenAsync();
        }

        bool unique = false;
        string? filter = null;
        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            unique = reader.GetBoolean(0);
            filter = reader.IsDBNull(1) ? null : reader.GetString(1);
            columns.Add(reader.GetString(2));
        }

        return (unique, filter, columns);
    }
}