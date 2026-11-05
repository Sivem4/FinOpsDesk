using System.Text.RegularExpressions;

using FinOpsDesk.Modules.Reconciliation;
using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Application.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Application.Invoices;
using FinOpsDesk.Modules.Reconciliation.Application.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Configurations;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Repositories;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace FinOpsDesk.IntegrationTests.Reconciliation.Persistence;

[Collection(SqlServerCollection.Name)]
public sealed class ReviewCaseAuditTests
{
    private static readonly DateTimeOffset OpenedAt = new(2026, 11, 5, 9, 0, 0, TimeSpan.Zero);

    private readonly SqlServerFixture _fixture;

    public ReviewCaseAuditTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Each_new_action_stores_the_actor_payload_and_decision_time()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 40m);
        await SeedAsync(invoice);
        OpenReviewCaseResponse opened = await OpenAsync(organizationId, invoice.Id, "operator-a", "kept");

        ReviewCaseAuditEntry openedEntry = await SingleAuditAsync(organizationId);
        Assert.Equal(ReviewCaseAuditAction.Opened, openedEntry.Action);
        Assert.Equal("operator-a", openedEntry.ActorId);
        Assert.Equal(OpenedAt, openedEntry.RecordedAt);
        Assert.Equal(ReviewCaseReason.NeedsInvestigation, openedEntry.Reason);
        Assert.Equal(invoice.Id, openedEntry.InvoiceId);
        Assert.Equal(ReviewCaseSource.Manual, openedEntry.SourceLabel);
        Assert.Equal("kept", openedEntry.Note);
        Assert.Null(openedEntry.Outcome);

        string token = await TokenAsync(opened.ReviewCase.ReviewCaseId);
        Result<ReviewCaseResponse> resolved = await ResolveAsync(
            organizationId,
            new ReviewCaseId(opened.ReviewCase.ReviewCaseId),
            token,
            "operator-b",
            "done");
        Assert.True(resolved.IsSuccess);

        await using ReconciliationDbContext context = _fixture.CreateContext();
        ReviewCaseAuditEntry[] entries = await context.ReviewCaseAuditEntries
            .Where(entry => entry.OrganizationId == organizationId)
            .OrderBy(entry => entry.Action)
            .ToArrayAsync();
        Assert.Equal(2, entries.Length);
        Assert.Equal("operator-a", entries[0].ActorId);
        Assert.Equal(OpenedAt, entries[0].RecordedAt);
        Assert.Equal(ReviewCaseAuditAction.Resolved, entries[1].Action);
        Assert.Equal("operator-b", entries[1].ActorId);
        Assert.Equal(resolved.Value.ClosedAt, entries[1].RecordedAt);
        Assert.Equal(ReviewCaseOutcome.NoActionRequired, entries[1].Outcome);
        Assert.Equal("done", entries[1].ResolutionNote);
        Assert.Null(entries[1].Reason);
        Assert.Null(entries[1].InvoiceId);
    }

    [Fact]
    public async Task Replay_by_another_actor_preserves_the_original_attribution()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 20m);
        await SeedAsync(invoice);
        OpenReviewCaseResponse opened = await OpenAsync(organizationId, invoice.Id, "operator-a", "kept");
        byte[] version = await RowVersionAsync(opened.ReviewCase.ReviewCaseId);

        Result<OpenReviewCaseResponse> replayed = await OpenRawAsync(
            organizationId,
            invoice.Id,
            "operator-b",
            "other");
        Assert.True(replayed.IsSuccess);
        Assert.False(replayed.Value.Created);

        ReviewCaseAuditEntry entry = await SingleAuditAsync(organizationId);
        Assert.Equal("operator-a", entry.ActorId);
        Assert.Equal(OpenedAt, entry.RecordedAt);
        Assert.Equal(version, await RowVersionAsync(opened.ReviewCase.ReviewCaseId));
    }

    [Fact]
    public async Task A_later_open_after_dismissal_gets_its_own_entry()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 15m);
        await SeedAsync(invoice);
        OpenReviewCaseResponse first = await OpenAsync(organizationId, invoice.Id, "operator-a", "first");
        string token = await TokenAsync(first.ReviewCase.ReviewCaseId);
        Assert.True((await DismissAsync(
            organizationId,
            new ReviewCaseId(first.ReviewCase.ReviewCaseId),
            token,
            "operator-a",
            "closed")).IsSuccess);

        OpenReviewCaseResponse second = await OpenAsync(
            organizationId,
            invoice.Id,
            "operator-b",
            "second",
            OpenedAt.AddHours(1));

        await using ReconciliationDbContext context = _fixture.CreateContext();
        ReviewCaseAuditEntry[] entries = await context.ReviewCaseAuditEntries
            .Where(entry => entry.OrganizationId == organizationId)
            .ToArrayAsync();
        Assert.Equal(3, entries.Length);
        Assert.Equal(2, entries.Count(entry => entry.Action == ReviewCaseAuditAction.Opened));
        ReviewCaseAuditEntry reopened = Assert.Single(
            entries,
            entry => entry.ReviewCaseId == new ReviewCaseId(second.ReviewCase.ReviewCaseId));
        Assert.Equal(ReviewCaseAuditAction.Opened, reopened.Action);
        Assert.Equal("operator-b", reopened.ActorId);
        Assert.Equal("second", reopened.Note);
        Assert.Equal(OpenedAt.AddHours(1), reopened.RecordedAt);
    }

    [Fact]
    public async Task Two_parallel_opens_leave_one_case_and_one_opened_entry()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 30m);
        await SeedAsync(invoice);

        Result<OpenReviewCaseResponse>[] results = await OpenTogetherAsync(organizationId, invoice.Id);

        Assert.Equal(1, results.Count(result => result.IsSuccess && result.Value.Created));
        Assert.All(results, result => Assert.True(result.IsSuccess));
        Assert.Equal(results[0].Value!.ReviewCase.ReviewCaseId, results[1].Value!.ReviewCase.ReviewCaseId);
        Assert.Equal(1, await CaseCountAsync(organizationId));
        ReviewCaseAuditEntry entry = await SingleAuditAsync(organizationId);
        Assert.Equal(ReviewCaseAuditAction.Opened, entry.Action);
        Assert.Contains(entry.ActorId, new[] { "operator-a", "operator-b" });
    }

    [Fact]
    public async Task Parallel_identical_resolve_and_dismiss_leave_one_terminal_entry()
    {
        OrganizationId resolvedOrganization = OrganizationId.New();
        Invoice resolvedInvoice = PersistenceTestData.CreateInvoice(resolvedOrganization, 11m);
        OrganizationId dismissedOrganization = OrganizationId.New();
        Invoice dismissedInvoice = PersistenceTestData.CreateInvoice(dismissedOrganization, 12m, "INV-DISMISS");
        await SeedAsync(resolvedInvoice, dismissedInvoice);
        OpenReviewCaseResponse resolvedCase = await OpenAsync(resolvedOrganization, resolvedInvoice.Id, "operator-a", null);
        OpenReviewCaseResponse dismissedCase = await OpenAsync(dismissedOrganization, dismissedInvoice.Id, "operator-a", null);

        Result<ReviewCaseResponse>[] resolved = await ResolveTogetherAsync(
            resolvedOrganization,
            new ReviewCaseId(resolvedCase.ReviewCase.ReviewCaseId),
            await TokenAsync(resolvedCase.ReviewCase.ReviewCaseId),
            "same",
            "same");
        Result<ReviewCaseResponse>[] dismissed = await DismissTogetherAsync(
            dismissedOrganization,
            new ReviewCaseId(dismissedCase.ReviewCase.ReviewCaseId),
            await TokenAsync(dismissedCase.ReviewCase.ReviewCaseId),
            "same");

        Assert.All(resolved, result => Assert.True(result.IsSuccess));
        Assert.All(dismissed, result => Assert.True(result.IsSuccess));
        Assert.Equal(2, await AuditCountAsync(resolvedOrganization));
        Assert.Equal(2, await AuditCountAsync(dismissedOrganization));
        await using ReconciliationDbContext context = _fixture.CreateContext();
        Assert.Equal(
            1,
            await context.ReviewCaseAuditEntries.CountAsync(entry =>
                entry.OrganizationId == resolvedOrganization
                && entry.Action == ReviewCaseAuditAction.Resolved));
        Assert.Equal(
            1,
            await context.ReviewCaseAuditEntries.CountAsync(entry =>
                entry.OrganizationId == dismissedOrganization
                && entry.Action == ReviewCaseAuditAction.Dismissed));
    }

    [Fact]
    public async Task Competing_terminal_decisions_keep_only_the_winner_entry()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 18m);
        await SeedAsync(invoice);
        OpenReviewCaseResponse opened = await OpenAsync(organizationId, invoice.Id, "operator-a", null);
        ReviewCaseId reviewCaseId = new(opened.ReviewCase.ReviewCaseId);
        string token = await TokenAsync(opened.ReviewCase.ReviewCaseId);

        Result<ReviewCaseResponse>[] crossed = await DecideTogetherAsync(
            organizationId,
            reviewCaseId,
            token,
            resolveNote: "resolved",
            dismissNote: "dismissed");
        Invoice secondInvoice = PersistenceTestData.CreateInvoice(organizationId, 19m, "INV-OTHER-DECISION");
        await SeedAsync(secondInvoice);
        OpenReviewCaseResponse second = await OpenAsync(organizationId, secondInvoice.Id, "operator-a", null);
        Result<ReviewCaseResponse>[] different = await ResolveTogetherAsync(
            organizationId,
            new ReviewCaseId(second.ReviewCase.ReviewCaseId),
            await TokenAsync(second.ReviewCase.ReviewCaseId),
            "alpha",
            "beta");

        Assert.Equal(1, crossed.Count(result => result.IsSuccess));
        Assert.Equal(1, crossed.Count(result =>
            result.IsFailure
            && result.Error!.Code == ReconciliationErrors.ReviewCaseTerminalConflict.Code));
        await using ReconciliationDbContext context = _fixture.CreateContext();
        ReviewCase stored = await context.ReviewCases.SingleAsync(candidate => candidate.Id == reviewCaseId);
        ReviewCaseAuditEntry terminal = await context.ReviewCaseAuditEntries.SingleAsync(entry =>
            entry.ReviewCaseId == reviewCaseId && entry.Action != ReviewCaseAuditAction.Opened);
        Assert.Equal(
            stored.Status == ReviewCaseStatus.Resolved
                ? ReviewCaseAuditAction.Resolved
                : ReviewCaseAuditAction.Dismissed,
            terminal.Action);
        Assert.Equal(stored.ClosedAt, terminal.RecordedAt);
        Assert.Equal(2, await context.ReviewCaseAuditEntries.CountAsync(entry => entry.ReviewCaseId == reviewCaseId));

        Assert.Equal(1, different.Count(result => result.IsSuccess));
        Assert.Contains(
            ReconciliationErrors.ReviewCaseTerminalConflict.Code,
            different.Where(result => result.IsFailure).Select(result => result.Error!.Code));
        Assert.Equal(
            1,
            await context.ReviewCaseAuditEntries.CountAsync(entry =>
                entry.ReviewCaseId == new ReviewCaseId(second.ReviewCase.ReviewCaseId)
                && entry.Action == ReviewCaseAuditAction.Resolved));
    }

    [Fact]
    public async Task A_foreign_subject_and_a_missing_actor_do_not_write()
    {
        OrganizationId owner = OrganizationId.New();
        OrganizationId other = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(owner, 9m);
        await SeedAsync(invoice);
        OpenReviewCaseResponse opened = await OpenAsync(owner, invoice.Id, "operator-a", "kept");

        Result<OpenReviewCaseResponse> foreign = await OpenRawAsync(
            other,
            invoice.Id,
            "operator-b",
            "foreign");
        Result<OpenReviewCaseResponse> missing = await OpenRawAsync(
            owner,
            invoice.Id,
            actorId: null,
            "missing");

        Assert.True(foreign.IsFailure);
        Assert.Equal(ReconciliationApplicationErrors.InvoiceNotFound.Code, foreign.Error.Code);
        Assert.True(missing.IsFailure);
        Assert.Equal(ReconciliationApplicationErrors.ActorRequired.Code, missing.Error.Code);
        Assert.Equal(0, await AuditCountAsync(other));
        ReviewCaseAuditEntry entry = await SingleAuditAsync(owner);
        Assert.Equal(opened.ReviewCase.ReviewCaseId, entry.ReviewCaseId.Value);
        Assert.Equal("operator-a", entry.ActorId);
    }

    [Fact]
    public async Task A_duplicate_audit_insert_rolls_the_case_change_back()
    {
        OrganizationId openedOrganization = OrganizationId.New();
        Invoice openedInvoice = PersistenceTestData.CreateInvoice(openedOrganization, 7m);
        OrganizationId resolvedOrganization = OrganizationId.New();
        Invoice resolvedInvoice = PersistenceTestData.CreateInvoice(resolvedOrganization, 8m, "INV-RESOLVE");
        OrganizationId dismissedOrganization = OrganizationId.New();
        Invoice dismissedInvoice = PersistenceTestData.CreateInvoice(dismissedOrganization, 6m, "INV-DISMISS-ATOMIC");
        await SeedAsync(openedInvoice, resolvedInvoice, dismissedInvoice);
        OpenReviewCaseResponse resolvedCase = await OpenAsync(resolvedOrganization, resolvedInvoice.Id, "operator-a", null);
        OpenReviewCaseResponse dismissedCase = await OpenAsync(dismissedOrganization, dismissedInvoice.Id, "operator-a", null);
        byte[] resolvedVersion = await RowVersionAsync(resolvedCase.ReviewCase.ReviewCaseId);
        byte[] dismissedVersion = await RowVersionAsync(dismissedCase.ReviewCase.ReviewCaseId);

        Exception openedFailure = await Assert.ThrowsAnyAsync<Exception>(() =>
            OpenRawAsync(openedOrganization, openedInvoice.Id, "operator-a", "atomic", duplicateAudit: true));
        (Result<ReviewCaseResponse> resolvedResult, Exception resolvedFailure) = await ResolveDuplicateAsync(
            resolvedOrganization,
            new ReviewCaseId(resolvedCase.ReviewCase.ReviewCaseId),
            ReviewCaseConcurrencyToken.Encode(resolvedVersion));
        (Result<ReviewCaseResponse> dismissedResult, Exception dismissedFailure) = await DismissDuplicateAsync(
            dismissedOrganization,
            new ReviewCaseId(dismissedCase.ReviewCase.ReviewCaseId),
            ReviewCaseConcurrencyToken.Encode(dismissedVersion));

        Assert.True(resolvedResult.IsFailure);
        Assert.Equal(
            ReconciliationApplicationErrors.ReviewCaseConcurrencyConflict.Code,
            resolvedResult.Error.Code);
        Assert.True(dismissedResult.IsFailure);
        Assert.Equal(
            ReconciliationApplicationErrors.ReviewCaseConcurrencyConflict.Code,
            dismissedResult.Error.Code);
        Assert.Contains(ReviewCaseAuditEntryConfiguration.ActionUniqueIndexName, openedFailure.ToString(), StringComparison.Ordinal);
        AssertAuditUniquenessIndex(resolvedFailure);
        AssertAuditUniquenessIndex(dismissedFailure);
        Assert.DoesNotContain(ReviewCaseConfiguration.OpenSubjectIndexName, openedFailure.ToString(), StringComparison.Ordinal);

        await using ReconciliationDbContext context = _fixture.CreateContext();
        Assert.Equal(0, await context.ReviewCases.CountAsync(reviewCase => reviewCase.OrganizationId == openedOrganization));
        Assert.Equal(0, await context.ReviewCaseAuditEntries.CountAsync(entry => entry.OrganizationId == openedOrganization));
        ReviewCase resolvedStored = await context.ReviewCases.SingleAsync(reviewCase =>
            reviewCase.Id == new ReviewCaseId(resolvedCase.ReviewCase.ReviewCaseId));
        ReviewCase dismissedStored = await context.ReviewCases.SingleAsync(reviewCase =>
            reviewCase.Id == new ReviewCaseId(dismissedCase.ReviewCase.ReviewCaseId));
        Assert.Equal(ReviewCaseStatus.Open, resolvedStored.Status);
        Assert.Null(resolvedStored.Outcome);
        Assert.Null(resolvedStored.ClosedAt);
        Assert.Equal(ReviewCaseStatus.Open, dismissedStored.Status);
        Assert.Null(dismissedStored.ClosedAt);
        Assert.Equal(resolvedVersion, context.Entry(resolvedStored).Property<byte[]>("RowVersion").CurrentValue);
        Assert.Equal(dismissedVersion, context.Entry(dismissedStored).Property<byte[]>("RowVersion").CurrentValue);
        Assert.Equal(
            1,
            await context.ReviewCaseAuditEntries.CountAsync(entry =>
                entry.OrganizationId == resolvedOrganization
                && entry.Action == ReviewCaseAuditAction.Opened));
        Assert.Equal(
            1,
            await context.ReviewCaseAuditEntries.CountAsync(entry =>
                entry.OrganizationId == dismissedOrganization
                && entry.Action == ReviewCaseAuditAction.Opened));
    }

    [Fact]
    public async Task Source_label_check_rejects_different_case_and_ef_reads_the_allowed_values()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice rejectedInvoice = PersistenceTestData.CreateInvoice(organizationId, 4m, "INV-CASE");
        Invoice manualInvoice = PersistenceTestData.CreateInvoice(organizationId, 4m, "INV-MANUAL");
        Invoice exactInvoice = PersistenceTestData.CreateInvoice(organizationId, 4m, "INV-EXACT");
        Invoice smartInvoice = PersistenceTestData.CreateInvoice(organizationId, 4m, "INV-SMART");
        await SeedAsync(rejectedInvoice, manualInvoice, exactInvoice, smartInvoice);
        ReviewCaseId rejectedCase = await SaveWithoutAuditAsync(organizationId, rejectedInvoice.Id);
        ReviewCaseId manualCase = await SaveWithoutAuditAsync(organizationId, manualInvoice.Id);
        ReviewCaseId exactCase = await SaveWithoutAuditAsync(organizationId, exactInvoice.Id);
        ReviewCaseId smartCase = await SaveWithoutAuditAsync(organizationId, smartInvoice.Id);

        SqlException rejected = await InsertRawAsync(
            organizationId,
            rejectedCase,
            action: 1,
            actorId: "operator-a",
            reason: 2,
            invoiceId: rejectedInvoice.Id.Value,
            sourceLabel: "MANUAL");
        await InsertRawAsync(
            organizationId,
            manualCase,
            action: 1,
            actorId: "operator-a",
            reason: 2,
            invoiceId: manualInvoice.Id.Value,
            sourceLabel: ReviewCaseSource.Manual.Value,
            expectFailure: false);
        await InsertRawAsync(
            organizationId,
            exactCase,
            action: 1,
            actorId: "operator-a",
            reason: 2,
            invoiceId: exactInvoice.Id.Value,
            sourceLabel: ReviewCaseSource.Exact1.Value,
            expectFailure: false);
        await InsertRawAsync(
            organizationId,
            smartCase,
            action: 1,
            actorId: "operator-a",
            reason: 2,
            invoiceId: smartInvoice.Id.Value,
            sourceLabel: ReviewCaseSource.Smart1.Value,
            expectFailure: false);

        Assert.Equal(547, rejected.Number);
        Assert.Contains(
            ReviewCaseAuditEntryConfiguration.OpenedShapeCheckName,
            rejected.Message,
            StringComparison.Ordinal);
        await using ReconciliationDbContext context = _fixture.CreateContext();
        ReviewCaseAuditEntry[] entries = await context.ReviewCaseAuditEntries
            .Where(entry => entry.OrganizationId == organizationId)
            .ToArrayAsync();
        Assert.Equal(3, entries.Length);
        Assert.Equal(ReviewCaseSource.Manual, Assert.Single(entries, entry => entry.ReviewCaseId == manualCase).SourceLabel);
        Assert.Equal(ReviewCaseSource.Exact1, Assert.Single(entries, entry => entry.ReviewCaseId == exactCase).SourceLabel);
        Assert.Equal(ReviewCaseSource.Smart1, Assert.Single(entries, entry => entry.ReviewCaseId == smartCase).SourceLabel);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Incremental_script_creates_a_working_trigger(bool idempotent)
    {
        string databaseName = "FinOpsDesk_IT_Upgrade_" + Guid.NewGuid().ToString("N");
        string connectionString = await CreateDatabaseAsync(databaseName);
        try
        {
            string script;
            string migrationId;
            await using (ReconciliationDbContext baseline = _fixture.CreateContext(connectionString))
            {
                string from = baseline.Database.GetMigrations().Single(name =>
                    name.EndsWith("_AddAllocationHistoryIndex", StringComparison.Ordinal));
                migrationId = baseline.Database.GetMigrations().Single(name =>
                    name.EndsWith("_AddReviewCaseAuditEntries", StringComparison.Ordinal));
                await baseline.Database.MigrateAsync(from);
                script = baseline.GetService<IMigrator>().GenerateScript(
                    from,
                    migrationId,
                    idempotent
                        ? MigrationsSqlGenerationOptions.Idempotent
                        : MigrationsSqlGenerationOptions.Default);
            }

            AssertScriptCreatesTriggerInItsOwnBatch(script);
            await ExecuteScriptAsync(connectionString, script);
            await AssertTriggerRejectsMutationAsync(connectionString);
            if (idempotent)
            {
                await ExecuteScriptAsync(connectionString, script);
                await AssertTriggerRejectsMutationAsync(connectionString);
            }

            Assert.Equal(1, await CountMigrationRowsAsync(connectionString, migrationId));
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
        }
    }

    [Fact]
    public async Task Writer_adds_the_entry_without_saving_or_opening_a_transaction()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 5m);
        await SeedAsync(invoice);
        await using ReconciliationDbContext context = _fixture.CreateContext();
        ReviewCase reviewCase = ReviewCase.Open(
            ReviewCaseId.New(),
            organizationId,
            ReviewCaseReason.NeedsInvestigation,
            invoice.Id,
            null,
            ReviewCaseSource.Manual,
            null,
            OpenedAt);
        context.ReviewCases.Add(reviewCase);
        var writer = new ReviewCaseAuditWriter(context);

        writer.AppendOpened(reviewCase, "operator-a");

        Assert.Null(context.Database.CurrentTransaction);
        Assert.Equal(EntityState.Added, context.Entry(context.ReviewCaseAuditEntries.Local.Single()).State);
        Assert.Equal(0, await AuditCountAsync(organizationId));
    }

    [Fact]
    public async Task Raw_sql_rejects_illegal_shapes_duplicates_and_mutation()
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 4m);
        Invoice shapeInvoice = PersistenceTestData.CreateInvoice(organizationId, 3m, "INV-SHAPE");
        await SeedAsync(invoice, shapeInvoice);
        OpenReviewCaseResponse opened = await OpenAsync(organizationId, invoice.Id, "operator-a", "kept");
        ReviewCaseId shapeCaseId = await SaveWithoutAuditAsync(organizationId, shapeInvoice.Id);
        ReviewCaseAuditEntry stored = await SingleAuditAsync(organizationId);

        SqlException missingReason = await InsertRawAsync(
            organizationId,
            shapeCaseId,
            action: 1,
            actorId: "operator-a",
            reason: null,
            invoiceId: invoice.Id.Value);
        SqlException paddedActor = await InsertRawAsync(
            organizationId,
            shapeCaseId,
            action: 1,
            actorId: " operator-a",
            reason: 2,
            invoiceId: invoice.Id.Value);
        SqlException nullActor = await InsertRawAsync(
            organizationId,
            shapeCaseId,
            action: 1,
            actorId: null,
            reason: 2,
            invoiceId: invoice.Id.Value);
        SqlException duplicate = await InsertRawAsync(
            organizationId,
            stored.ReviewCaseId,
            action: 1,
            actorId: "operator-a",
            reason: 2,
            invoiceId: invoice.Id.Value);
        await InsertRawAsync(
            organizationId,
            stored.ReviewCaseId,
            action: 2,
            actorId: "operator-a",
            reason: null,
            invoiceId: null,
            outcome: 2,
            expectFailure: false);
        SqlException secondTerminal = await InsertRawAsync(
            organizationId,
            stored.ReviewCaseId,
            action: 3,
            actorId: "operator-a",
            reason: null,
            invoiceId: null);
        SqlException updated = (await ExecuteAsync(
            """
            UPDATE [reconciliation].[ReviewCaseAuditEntries]
            SET [ActorId] = N'changed'
            WHERE [Id] = @id
            """,
            ("@id", stored.Id.Value)))!;
        SqlException deleted = (await ExecuteAsync(
            """
            DELETE FROM [reconciliation].[ReviewCaseAuditEntries]
            WHERE [Id] = @id
            """,
            ("@id", stored.Id.Value)))!;

        Assert.Equal(547, missingReason.Number);
        Assert.Contains(ReviewCaseAuditEntryConfiguration.OpenedShapeCheckName, missingReason.Message, StringComparison.Ordinal);
        Assert.Equal(547, paddedActor.Number);
        Assert.Contains(ReviewCaseAuditEntryConfiguration.ActorIdCheckName, paddedActor.Message, StringComparison.Ordinal);
        Assert.Equal(515, nullActor.Number);
        Assert.True(duplicate.Number is 2601 or 2627);
        Assert.Contains(ReviewCaseAuditEntryConfiguration.ActionUniqueIndexName, duplicate.Message, StringComparison.Ordinal);
        Assert.True(secondTerminal.Number is 2601 or 2627);
        Assert.Contains(ReviewCaseAuditEntryConfiguration.TerminalUniqueIndexName, secondTerminal.Message, StringComparison.Ordinal);
        Assert.Equal(50002, updated.Number);
        Assert.Equal(50002, deleted.Number);
        await using ReconciliationDbContext afterContext = _fixture.CreateContext();
        ReviewCaseAuditEntry after = await afterContext.ReviewCaseAuditEntries
            .SingleAsync(entry => entry.Id == stored.Id);
        Assert.Equal("operator-a", after.ActorId);
        Assert.Equal("kept", after.Note);
    }

    [Fact]
    public async Task Migration_preserves_historical_cases_without_inventing_history()
    {
        string databaseName = "FinOpsDesk_IT_Upgrade_" + Guid.NewGuid().ToString("N");
        string connectionString = await CreateDatabaseAsync(databaseName);
        try
        {
            OrganizationId organizationId = OrganizationId.New();
            Invoice openInvoice = PersistenceTestData.CreateInvoice(organizationId, 3m, "INV-HIST-OPEN");
            Invoice closedInvoice = PersistenceTestData.CreateInvoice(organizationId, 2m, "INV-HIST-CLOSED");
            ReviewCase historicalOpen = ReviewCase.Open(
                ReviewCaseId.New(),
                organizationId,
                ReviewCaseReason.NeedsInvestigation,
                openInvoice.Id,
                null,
                null,
                null,
                OpenedAt);
            ReviewCase historicalClosed = ReviewCase.Open(
                ReviewCaseId.New(),
                organizationId,
                ReviewCaseReason.NeedsInvestigation,
                closedInvoice.Id,
                null,
                null,
                null,
                OpenedAt);
            Assert.True(historicalClosed.Dismiss("kept", OpenedAt.AddMinutes(1)).IsSuccess);

            await using (ReconciliationDbContext baseline = _fixture.CreateContext(connectionString))
            {
                string migration = baseline.Database.GetMigrations()
                    .Single(name => name.EndsWith("_AddAllocationHistoryIndex", StringComparison.Ordinal));
                await baseline.Database.MigrateAsync(migration);
                baseline.AddRange(openInvoice, closedInvoice, historicalOpen, historicalClosed);
                await baseline.SaveChangesAsync();
            }

            await using (ReconciliationDbContext upgraded = _fixture.CreateContext(connectionString))
            {
                await upgraded.Database.MigrateAsync();
                Assert.Equal(0, await upgraded.ReviewCaseAuditEntries.CountAsync());
                Assert.Equal(2, await upgraded.ReviewCases.CountAsync());
            }

            await using ServiceProvider provider = CreateProvider(connectionString);
            await using AsyncServiceScope scope = provider.CreateAsyncScope();
            string openToken = await TokenAsync(connectionString, historicalOpen.Id);
            DismissReviewCaseHandler dismiss = CreateDismissHandler(
                scope.ServiceProvider,
                organizationId,
                "operator-a");
            Result<ReviewCaseResponse> closed = await dismiss.Handle(
                new DismissReviewCaseCommand(historicalOpen.Id, "now", openToken),
                CancellationToken.None);
            Assert.True(closed.IsSuccess);
            string closedToken = await TokenAsync(connectionString, historicalClosed.Id);
            await using AsyncServiceScope replayScope = provider.CreateAsyncScope();
            DismissReviewCaseHandler replay = CreateDismissHandler(
                replayScope.ServiceProvider,
                organizationId,
                "operator-b");
            Result<ReviewCaseResponse> replayed = await replay.Handle(
                new DismissReviewCaseCommand(historicalClosed.Id, "kept", closedToken),
                CancellationToken.None);
            Assert.True(replayed.IsSuccess);

            await using ReconciliationDbContext context = _fixture.CreateContext(connectionString);
            ReviewCaseAuditEntry entry = await context.ReviewCaseAuditEntries.SingleAsync();
            Assert.Equal(historicalOpen.Id, entry.ReviewCaseId);
            Assert.Equal(ReviewCaseAuditAction.Dismissed, entry.Action);
            Assert.Equal("operator-a", entry.ActorId);
            Assert.Equal(0, await context.ReviewCaseAuditEntries.CountAsync(candidate =>
                candidate.Action == ReviewCaseAuditAction.Opened));
            Assert.Equal(0, await context.ReviewCaseAuditEntries.CountAsync(candidate =>
                candidate.ReviewCaseId == historicalClosed.Id));
        }
        finally
        {
            await DropDatabaseAsync(databaseName);
        }
    }

    private async Task<OpenReviewCaseResponse> OpenAsync(
        OrganizationId organizationId,
        InvoiceId invoiceId,
        string actorId,
        string? note,
        DateTimeOffset? openedAt = null)
    {
        Result<OpenReviewCaseResponse> result = await OpenRawAsync(
            organizationId,
            invoiceId,
            actorId,
            note,
            openedAt);
        Assert.True(result.IsSuccess);
        return result.Value;
    }

    private async Task<Result<OpenReviewCaseResponse>> OpenRawAsync(
        OrganizationId organizationId,
        InvoiceId invoiceId,
        string? actorId,
        string? note,
        DateTimeOffset? openedAt = null,
        bool duplicateAudit = false)
    {
        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        OpenReviewCaseHandler handler = CreateOpenHandler(
            scope.ServiceProvider,
            organizationId,
            actorId,
            openedAt ?? OpenedAt,
            duplicateAudit);
        return await handler.Handle(
            new OpenReviewCaseCommand(
                ReviewCaseReason.NeedsInvestigation,
                invoiceId,
                null,
                "manual",
                note),
            CancellationToken.None);
    }

    private async Task<Result<ReviewCaseResponse>> ResolveAsync(
        OrganizationId organizationId,
        ReviewCaseId reviewCaseId,
        string token,
        string actorId,
        string note,
        bool duplicateAudit = false)
    {
        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        ResolveReviewCaseHandler handler = CreateResolveHandler(
            scope.ServiceProvider,
            organizationId,
            actorId,
            duplicateAudit: duplicateAudit);
        return await handler.Handle(
            new ResolveReviewCaseCommand(
                reviewCaseId,
                ReviewCaseOutcome.NoActionRequired,
                null,
                note,
                token),
            CancellationToken.None);
    }

    private async Task<Result<ReviewCaseResponse>> DismissAsync(
        OrganizationId organizationId,
        ReviewCaseId reviewCaseId,
        string token,
        string actorId,
        string note,
        bool duplicateAudit = false)
    {
        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        DismissReviewCaseHandler handler = CreateDismissHandler(
            scope.ServiceProvider,
            organizationId,
            actorId,
            duplicateAudit);
        return await handler.Handle(
            new DismissReviewCaseCommand(reviewCaseId, note, token),
            CancellationToken.None);
    }

    private async Task<Result<OpenReviewCaseResponse>[]> OpenTogetherAsync(
        OrganizationId organizationId,
        InvoiceId invoiceId)
    {
        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        using Barrier gate = new(2);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        Task<Result<OpenReviewCaseResponse>> Run(string actorId, string note)
        {
            return Task.Run(
                async () =>
                {
                    await using AsyncServiceScope scope = provider.CreateAsyncScope();
                    OpenReviewCaseHandler handler = CreateOpenHandler(
                        scope.ServiceProvider,
                        organizationId,
                        actorId,
                        OpenedAt,
                        duplicateAudit: false,
                        new GatedUnitOfWork(scope.ServiceProvider.GetRequiredService<IUnitOfWork>(), gate));
                    return await handler.Handle(
                        new OpenReviewCaseCommand(
                            ReviewCaseReason.NeedsInvestigation,
                            invoiceId,
                            null,
                            "manual",
                            note),
                        timeout.Token);
                },
                timeout.Token);
        }

        return await Task.WhenAll(Run("operator-a", "alpha"), Run("operator-b", "beta"));
    }

    private Task<Result<ReviewCaseResponse>[]> ResolveTogetherAsync(
        OrganizationId organizationId,
        ReviewCaseId reviewCaseId,
        string token,
        string firstNote,
        string secondNote)
    {
        return DecideAsync(
            organizationId,
            reviewCaseId,
            token,
            (services, gate) => CreateResolveHandler(
                services,
                organizationId,
                "operator-a",
                new GatedUnitOfWork(services.GetRequiredService<IUnitOfWork>(), gate)),
            (services, gate) => CreateResolveHandler(
                services,
                organizationId,
                "operator-b",
                new GatedUnitOfWork(services.GetRequiredService<IUnitOfWork>(), gate)),
            firstNote,
            secondNote,
            dismiss: false);
    }

    private Task<Result<ReviewCaseResponse>[]> DismissTogetherAsync(
        OrganizationId organizationId,
        ReviewCaseId reviewCaseId,
        string token,
        string note)
    {
        return DecideAsync(
            organizationId,
            reviewCaseId,
            token,
            (services, gate) => CreateDismissHandler(
                services,
                organizationId,
                "operator-a",
                duplicateAudit: false,
                new GatedUnitOfWork(services.GetRequiredService<IUnitOfWork>(), gate)),
            (services, gate) => CreateDismissHandler(
                services,
                organizationId,
                "operator-b",
                duplicateAudit: false,
                new GatedUnitOfWork(services.GetRequiredService<IUnitOfWork>(), gate)),
            note,
            note,
            dismiss: true);
    }

    private Task<Result<ReviewCaseResponse>[]> DecideTogetherAsync(
        OrganizationId organizationId,
        ReviewCaseId reviewCaseId,
        string token,
        string resolveNote,
        string dismissNote)
    {
        return DecideAsync(
            organizationId,
            reviewCaseId,
            token,
            (services, gate) => CreateResolveHandler(
                services,
                organizationId,
                "operator-a",
                new GatedUnitOfWork(services.GetRequiredService<IUnitOfWork>(), gate)),
            (services, gate) => CreateDismissHandler(
                services,
                organizationId,
                "operator-b",
                duplicateAudit: false,
                new GatedUnitOfWork(services.GetRequiredService<IUnitOfWork>(), gate)),
            resolveNote,
            dismissNote,
            dismiss: false,
            mixed: true);
    }

    private async Task<Result<ReviewCaseResponse>[]> DecideAsync(
        OrganizationId organizationId,
        ReviewCaseId reviewCaseId,
        string token,
        Func<IServiceProvider, Barrier, object> firstFactory,
        Func<IServiceProvider, Barrier, object> secondFactory,
        string firstNote,
        string secondNote,
        bool dismiss,
        bool mixed = false)
    {
        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        using Barrier gate = new(2);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        Task<Result<ReviewCaseResponse>> Run(Func<IServiceProvider, Barrier, object> factory, string note, bool dismissing)
        {
            return Task.Run(
                async () =>
                {
                    await using AsyncServiceScope scope = provider.CreateAsyncScope();
                    object handler = factory(scope.ServiceProvider, gate);
                    if (dismissing)
                    {
                        return await ((DismissReviewCaseHandler)handler).Handle(
                            new DismissReviewCaseCommand(reviewCaseId, note, token),
                            timeout.Token);
                    }

                    return await ((ResolveReviewCaseHandler)handler).Handle(
                        new ResolveReviewCaseCommand(
                            reviewCaseId,
                            ReviewCaseOutcome.NoActionRequired,
                            null,
                            note,
                            token),
                        timeout.Token);
                },
                timeout.Token);
        }

        bool secondDismisses = mixed || dismiss;
        bool firstDismisses = dismiss && !mixed;
        return await Task.WhenAll(
            Run(firstFactory, firstNote, firstDismisses),
            Run(secondFactory, secondNote, secondDismisses));
    }

    private static OpenReviewCaseHandler CreateOpenHandler(
        IServiceProvider services,
        OrganizationId organizationId,
        string? actorId,
        DateTimeOffset openedAt,
        bool duplicateAudit,
        IUnitOfWork? unitOfWork = null)
    {
        ReconciliationDbContext context = services.GetRequiredService<ReconciliationDbContext>();
        return new OpenReviewCaseHandler(
            new TestCurrentOrganizationContext(organizationId),
            new TestCurrentActorContext(actorId),
            services.GetRequiredService<IInvoiceRepository>(),
            services.GetRequiredService<IBankTransactionRepository>(),
            services.GetRequiredService<IReviewCaseRepository>(),
            services.GetRequiredService<IReviewCaseSnapshotLookup>(),
            services.GetRequiredService<IReviewCaseOpenConflict>(),
            services.GetRequiredService<IReviewCaseOpenAttemptLookup>(),
            services.GetRequiredService<IReviewCaseOpenAttemptWriter>(),
            services.GetRequiredService<IReviewCaseOpenAttemptConflict>(),
            duplicateAudit
                ? new DuplicateReviewCaseAuditWriter(context)
                : services.GetRequiredService<IReviewCaseAuditWriter>(),
            unitOfWork ?? services.GetRequiredService<IUnitOfWork>(),
            new FixedTimeProvider(openedAt));
    }

    private static ResolveReviewCaseHandler CreateResolveHandler(
        IServiceProvider services,
        OrganizationId organizationId,
        string actorId,
        IUnitOfWork? unitOfWork = null,
        bool duplicateAudit = false)
    {
        ReconciliationDbContext context = services.GetRequiredService<ReconciliationDbContext>();
        return new ResolveReviewCaseHandler(
            new TestCurrentOrganizationContext(organizationId),
            new TestCurrentActorContext(actorId),
            services.GetRequiredService<IReviewCaseRepository>(),
            services.GetRequiredService<IReviewCaseSnapshotLookup>(),
            services.GetRequiredService<IAllocationLinkLookup>(),
            duplicateAudit
                ? new DuplicateReviewCaseAuditWriter(context)
                : services.GetRequiredService<IReviewCaseAuditWriter>(),
            services.GetRequiredService<IReviewCaseAuditConflict>(),
            unitOfWork ?? services.GetRequiredService<IUnitOfWork>(),
            new FixedTimeProvider(OpenedAt.AddMinutes(10)));
    }

    private static DismissReviewCaseHandler CreateDismissHandler(
        IServiceProvider services,
        OrganizationId organizationId,
        string? actorId = null,
        bool duplicateAudit = false,
        IUnitOfWork? unitOfWork = null)
    {
        ReconciliationDbContext context = services.GetRequiredService<ReconciliationDbContext>();
        return new DismissReviewCaseHandler(
            new TestCurrentOrganizationContext(organizationId),
            new TestCurrentActorContext(actorId),
            services.GetRequiredService<IReviewCaseRepository>(),
            services.GetRequiredService<IReviewCaseSnapshotLookup>(),
            duplicateAudit
                ? new DuplicateReviewCaseAuditWriter(context)
                : services.GetRequiredService<IReviewCaseAuditWriter>(),
            services.GetRequiredService<IReviewCaseAuditConflict>(),
            unitOfWork ?? services.GetRequiredService<IUnitOfWork>(),
            new FixedTimeProvider(OpenedAt.AddMinutes(10)));
    }

    private async Task<(Result<ReviewCaseResponse> Result, Exception Failure)> ResolveDuplicateAsync(
        OrganizationId organizationId,
        ReviewCaseId reviewCaseId,
        string token)
    {
        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        ObservingUnitOfWork observed = new(scope.ServiceProvider.GetRequiredService<IUnitOfWork>());
        ResolveReviewCaseHandler handler = CreateResolveHandler(
            scope.ServiceProvider,
            organizationId,
            "operator-a",
            observed,
            duplicateAudit: true);
        Result<ReviewCaseResponse> result = await handler.Handle(
            new ResolveReviewCaseCommand(
                reviewCaseId,
                ReviewCaseOutcome.NoActionRequired,
                null,
                "atomic",
                token),
            CancellationToken.None);
        return (result, Assert.IsAssignableFrom<Exception>(observed.Failure));
    }

    private async Task<(Result<ReviewCaseResponse> Result, Exception Failure)> DismissDuplicateAsync(
        OrganizationId organizationId,
        ReviewCaseId reviewCaseId,
        string token)
    {
        await using ServiceProvider provider = _fixture.CreateServiceProvider();
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        ObservingUnitOfWork observed = new(scope.ServiceProvider.GetRequiredService<IUnitOfWork>());
        DismissReviewCaseHandler handler = CreateDismissHandler(
            scope.ServiceProvider,
            organizationId,
            "operator-a",
            duplicateAudit: true,
            observed);
        Result<ReviewCaseResponse> result = await handler.Handle(
            new DismissReviewCaseCommand(reviewCaseId, "atomic", token),
            CancellationToken.None);
        return (result, Assert.IsAssignableFrom<Exception>(observed.Failure));
    }

    private async Task<ReviewCaseId> SaveWithoutAuditAsync(
        OrganizationId organizationId,
        InvoiceId invoiceId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        ReviewCase reviewCase = ReviewCase.Open(
            ReviewCaseId.New(),
            organizationId,
            ReviewCaseReason.NeedsInvestigation,
            invoiceId,
            null,
            null,
            null,
            OpenedAt);
        context.ReviewCases.Add(reviewCase);
        await context.SaveChangesAsync();
        return reviewCase.Id;
    }

    private async Task SeedAsync(params object[] entities)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        context.AddRange(entities);
        await context.SaveChangesAsync();
    }

    private async Task<ReviewCaseAuditEntry> SingleAuditAsync(OrganizationId organizationId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        return await context.ReviewCaseAuditEntries.SingleAsync(entry => entry.OrganizationId == organizationId);
    }

    private async Task<int> AuditCountAsync(OrganizationId organizationId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        return await context.ReviewCaseAuditEntries.CountAsync(entry => entry.OrganizationId == organizationId);
    }

    private async Task<int> CaseCountAsync(OrganizationId organizationId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        return await context.ReviewCases.CountAsync(reviewCase => reviewCase.OrganizationId == organizationId);
    }

    private async Task<string> TokenAsync(Guid reviewCaseId)
    {
        return ReviewCaseConcurrencyToken.Encode(await RowVersionAsync(reviewCaseId));
    }

    private async Task<byte[]> RowVersionAsync(Guid reviewCaseId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        ReviewCase reviewCase = await context.ReviewCases.SingleAsync(candidate =>
            candidate.Id == new ReviewCaseId(reviewCaseId));
        return context.Entry(reviewCase).Property<byte[]>("RowVersion").CurrentValue!.ToArray();
    }

    private async Task<string> TokenAsync(string connectionString, ReviewCaseId reviewCaseId)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext(connectionString);
        ReviewCase reviewCase = await context.ReviewCases.SingleAsync(candidate => candidate.Id == reviewCaseId);
        return ReviewCaseConcurrencyToken.Encode(
            context.Entry(reviewCase).Property<byte[]>("RowVersion").CurrentValue!);
    }

    private async Task<SqlException> InsertRawAsync(
        OrganizationId organizationId,
        ReviewCaseId reviewCaseId,
        int action,
        string? actorId,
        int? reason,
        Guid? invoiceId,
        int? outcome = null,
        string? sourceLabel = null,
        bool expectFailure = true)
    {
        const string sql =
            """
            INSERT INTO [reconciliation].[ReviewCaseAuditEntries]
                ([Id], [OrganizationId], [ReviewCaseId], [Action], [ActorId], [RecordedAt],
                 [Reason], [InvoiceId], [BankTransactionId], [SourceLabel], [Note],
                 [Outcome], [AllocationId], [ResolutionNote])
            VALUES
                (@id, @organizationId, @reviewCaseId, @action, @actorId, @recordedAt,
                 @reason, @invoiceId, NULL, @sourceLabel, NULL,
                 @outcome, NULL, NULL)
            """;
        SqlException? failure = await ExecuteAsync(
            sql,
            ("@id", Guid.NewGuid()),
            ("@organizationId", organizationId.Value),
            ("@reviewCaseId", reviewCaseId.Value),
            ("@action", action),
            ("@actorId", (object?)actorId ?? DBNull.Value),
            ("@recordedAt", OpenedAt),
            ("@reason", (object?)reason ?? DBNull.Value),
            ("@invoiceId", (object?)invoiceId ?? DBNull.Value),
            ("@sourceLabel", (object?)sourceLabel ?? DBNull.Value),
            ("@outcome", (object?)outcome ?? DBNull.Value));
        if (!expectFailure)
        {
            Assert.Null(failure);
            return null!;
        }

        return failure!;
    }

    private async Task<SqlException?> ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using ReconciliationDbContext context = _fixture.CreateContext();
        await context.Database.OpenConnectionAsync();
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object value) in parameters)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }

        try
        {
            await command.ExecuteNonQueryAsync();
            return null;
        }
        catch (SqlException exception)
        {
            return exception;
        }
    }

    private static void AssertAuditUniquenessIndex(Exception exception)
    {
        string text = exception.ToString();
        Assert.True(
            text.Contains(ReviewCaseAuditEntryConfiguration.ActionUniqueIndexName, StringComparison.Ordinal)
            || text.Contains(ReviewCaseAuditEntryConfiguration.TerminalUniqueIndexName, StringComparison.Ordinal),
            text);
        Assert.DoesNotContain(ReviewCaseConfiguration.OpenSubjectIndexName, text, StringComparison.Ordinal);
    }

    private static ServiceProvider CreateProvider(string connectionString)
    {
        ServiceCollection services = new();
        services.AddReconciliationModule(connectionString);
        return services.BuildServiceProvider();
    }

    private static void AssertScriptCreatesTriggerInItsOwnBatch(string script)
    {
        Assert.Contains("EXEC(N'CREATE TRIGGER [reconciliation].[TR_ReviewCaseAuditEntries_RejectMutation]", script, StringComparison.Ordinal);
        Assert.Contains("THROW 50002", script, StringComparison.Ordinal);
        foreach (string batch in SplitBatches(script))
        {
            Assert.False(
                batch.TrimStart().StartsWith("CREATE TRIGGER", StringComparison.OrdinalIgnoreCase),
                batch);
        }
    }

    private static async Task ExecuteScriptAsync(string connectionString, string script)
    {
        await using SqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        int batchNumber = 0;
        foreach (string batch in SplitBatches(script))
        {
            batchNumber++;
            await using SqlCommand command = new(batch, connection);
            try
            {
                await command.ExecuteNonQueryAsync();
            }
            catch (SqlException exception)
            {
                throw new InvalidOperationException(
                    "Batch " + batchNumber + " failed: " + exception.Message + Environment.NewLine + batch,
                    exception);
            }
        }
    }

    private async Task AssertTriggerRejectsMutationAsync(string connectionString)
    {
        OrganizationId organizationId = OrganizationId.New();
        Invoice invoice = PersistenceTestData.CreateInvoice(organizationId, 1m, "INV-" + Guid.NewGuid().ToString("N")[..8]);
        ReviewCase reviewCase = ReviewCase.Open(
            ReviewCaseId.New(),
            organizationId,
            ReviewCaseReason.NeedsInvestigation,
            invoice.Id,
            null,
            ReviewCaseSource.Manual,
            null,
            OpenedAt);
        ReviewCaseAuditEntry entry = ReviewCaseAuditEntry.ForOpened(
            ReviewCaseAuditEntryId.New(),
            reviewCase,
            "operator-a");
        await using (ReconciliationDbContext context = _fixture.CreateContext(connectionString))
        {
            context.AddRange(invoice, reviewCase, entry);
            await context.SaveChangesAsync();
            Assert.Equal(
                1,
                await ScalarAsync(
                    connectionString,
                    """
                    SELECT COUNT(*)
                    FROM sys.triggers
                    WHERE name = N'TR_ReviewCaseAuditEntries_RejectMutation'
                      AND is_disabled = 0
                    """));
        }

        SqlException updated = await ExecuteOnAsync(
            connectionString,
            """
            UPDATE [reconciliation].[ReviewCaseAuditEntries]
            SET [ActorId] = N'changed'
            WHERE [Id] = @id
            """,
            ("@id", entry.Id.Value));
        SqlException deleted = await ExecuteOnAsync(
            connectionString,
            """
            DELETE FROM [reconciliation].[ReviewCaseAuditEntries]
            WHERE [Id] = @id
            """,
            ("@id", entry.Id.Value));
        Assert.Equal(50002, updated.Number);
        Assert.Equal(50002, deleted.Number);
        await using ReconciliationDbContext after = _fixture.CreateContext(connectionString);
        ReviewCaseAuditEntry stored = await after.ReviewCaseAuditEntries.SingleAsync(candidate => candidate.Id == entry.Id);
        Assert.Equal("operator-a", stored.ActorId);
    }

    private static async Task<int> CountMigrationRowsAsync(string connectionString, string migrationId)
    {
        return await ScalarAsync(
            connectionString,
            """
            SELECT COUNT(*)
            FROM [reconciliation].[__EFMigrationsHistory]
            WHERE [MigrationId] = @id
            """,
            ("@id", migrationId));
    }

    private static async Task<int> ScalarAsync(
        string connectionString,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using SqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using SqlCommand command = new(sql, connection);
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (int)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<SqlException> ExecuteOnAsync(
        string connectionString,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using SqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using SqlCommand command = new(sql, connection);
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        try
        {
            await command.ExecuteNonQueryAsync();
        }
        catch (SqlException exception)
        {
            return exception;
        }

        throw new InvalidOperationException("The statement was accepted.");
    }

    private static IEnumerable<string> SplitBatches(string script)
    {
        return Regex.Split(script, @"^\s*GO\s*$", RegexOptions.Multiline)
            .Where(batch => !string.IsNullOrWhiteSpace(batch));
    }

    private async Task<string> CreateDatabaseAsync(string databaseName)
    {
        SqlConnectionStringBuilder database = new(_fixture.ConnectionString)
        {
            InitialCatalog = databaseName,
        };
        SqlConnectionStringBuilder master = new(database.ConnectionString)
        {
            InitialCatalog = "master",
        };
        await using SqlConnection connection = new(master.ConnectionString);
        await connection.OpenAsync();
        await using SqlCommand command = new("CREATE DATABASE [" + databaseName + "]", connection);
        await command.ExecuteNonQueryAsync();
        return database.ConnectionString;
    }

    private async Task DropDatabaseAsync(string databaseName)
    {
        SqlConnectionStringBuilder master = new(_fixture.ConnectionString)
        {
            InitialCatalog = "master",
        };
        await using SqlConnection connection = new(master.ConnectionString);
        await connection.OpenAsync();
        await using SqlCommand command = new(
            "ALTER DATABASE [" + databaseName + "] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE ["
            + databaseName + "];",
            connection);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class DuplicateReviewCaseAuditWriter : IReviewCaseAuditWriter
    {
        private readonly ReviewCaseAuditWriter _inner;

        public DuplicateReviewCaseAuditWriter(ReconciliationDbContext context)
        {
            _inner = new ReviewCaseAuditWriter(context);
        }

        public void AppendOpened(ReviewCase reviewCase, string actorId)
        {
            _inner.AppendOpened(reviewCase, actorId);
            _inner.AppendOpened(reviewCase, actorId);
        }

        public void AppendResolved(ReviewCase reviewCase, string actorId)
        {
            _inner.AppendResolved(reviewCase, actorId);
            _inner.AppendResolved(reviewCase, actorId);
        }

        public void AppendDismissed(ReviewCase reviewCase, string actorId)
        {
            _inner.AppendDismissed(reviewCase, actorId);
            _inner.AppendDismissed(reviewCase, actorId);
        }
    }

    private sealed class ObservingUnitOfWork : IUnitOfWork
    {
        private readonly IUnitOfWork _inner;

        public ObservingUnitOfWork(IUnitOfWork inner)
        {
            _inner = inner;
        }

        public Exception? Failure { get; private set; }

        public async Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            try
            {
                await _inner.SaveChangesAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                Failure = exception;
                throw;
            }
        }
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