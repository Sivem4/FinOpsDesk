using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Application.Allocations;
using FinOpsDesk.Modules.Reconciliation.Application.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.UnitTests.Reconciliation.Application.ReviewCases;

public sealed class OpenReviewCaseHandlerTests
{
    private static readonly OrganizationId OrganizationId =
        new(new Guid("11111111-1111-1111-1111-111111111111"));

    private static readonly OrganizationId OtherOrganizationId =
        new(new Guid("22222222-2222-2222-2222-222222222222"));

    [Fact]
    public async Task Handle_should_reject_an_invalid_shape_before_reading_the_organization()
    {
        RecordingOrganizationContext organization = new(OrganizationId);
        OpenReviewCaseHandler handler = CreateHandler(organization, new FakeInvoiceRepository());

        Result<OpenReviewCaseResponse> result = await handler.Handle(
            new OpenReviewCaseCommand(
                ReviewCaseReason.CompetingMatch,
                null,
                null,
                "manual",
                "note"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(
            ReconciliationApplicationErrors.ReviewCaseInvalidRequest.Code,
            result.Error!.Code);
        Assert.Equal(0, organization.ReadCount);
    }

    [Fact]
    public async Task Handle_should_reject_an_unknown_source_and_an_oversized_note()
    {
        OpenReviewCaseHandler handler = CreateHandler(
            new RecordingOrganizationContext(OrganizationId),
            new FakeInvoiceRepository());
        InvoiceId invoiceId = InvoiceId.New();

        Result<OpenReviewCaseResponse> source = await handler.Handle(
            new OpenReviewCaseCommand(
                ReviewCaseReason.NeedsInvestigation,
                invoiceId,
                null,
                "ai",
                null),
            CancellationToken.None);
        Result<OpenReviewCaseResponse> note = await handler.Handle(
            new OpenReviewCaseCommand(
                ReviewCaseReason.NeedsInvestigation,
                invoiceId,
                null,
                null,
                new string('n', ReviewCase.MaxNoteLength + 1)),
            CancellationToken.None);

        Assert.Equal(ReconciliationApplicationErrors.ReviewCaseInvalidRequest.Code, source.Error!.Code);
        Assert.Equal(ReconciliationApplicationErrors.ReviewCaseInvalidRequest.Code, note.Error!.Code);
    }

    [Fact]
    public async Task Handle_should_report_a_missing_invoice_for_the_current_organization()
    {
        RecordingOrganizationContext organization = new(OrganizationId);
        FakeInvoiceRepository invoices = new();
        invoices.Add(ReviewCaseHandlerTestDoubles.CreateInvoice(OtherOrganizationId));
        OpenReviewCaseHandler handler = CreateHandler(organization, invoices);
        using CancellationTokenSource cancellation = new();

        Result<OpenReviewCaseResponse> result = await handler.Handle(
            new OpenReviewCaseCommand(
                ReviewCaseReason.NeedsInvestigation,
                InvoiceId.New(),
                null,
                null,
                null),
            cancellation.Token);

        Assert.Equal(ReconciliationApplicationErrors.InvoiceNotFound.Code, result.Error!.Code);
        Assert.Equal(OrganizationId, invoices.OrganizationId);
        Assert.Equal(cancellation.Token, invoices.CancellationToken);
    }

    [Fact]
    public async Task Handle_should_report_a_missing_bank_transaction_for_the_current_organization()
    {
        Invoice invoice = ReviewCaseHandlerTestDoubles.CreateInvoice(OrganizationId);
        FakeInvoiceRepository invoices = new();
        invoices.Add(invoice);
        FakeBankTransactionRepository bankTransactions = new();
        bankTransactions.Add(ReviewCaseHandlerTestDoubles.CreateBankTransaction(OtherOrganizationId));
        OpenReviewCaseHandler handler = CreateHandler(
            new RecordingOrganizationContext(OrganizationId),
            invoices,
            bankTransactions);

        Result<OpenReviewCaseResponse> result = await handler.Handle(
            new OpenReviewCaseCommand(
                ReviewCaseReason.CompetingMatch,
                invoice.Id,
                BankTransactionId.New(),
                null,
                null),
            CancellationToken.None);

        Assert.Equal(
            ReconciliationApplicationErrors.BankTransactionNotFound.Code,
            result.Error!.Code);
        Assert.Equal(OrganizationId, bankTransactions.OrganizationId);
    }

    [Fact]
    public async Task Handle_should_create_the_first_open_case_with_the_context_clock()
    {
        Invoice invoice = ReviewCaseHandlerTestDoubles.CreateInvoice(OrganizationId);
        BankTransaction bankTransaction = ReviewCaseHandlerTestDoubles.CreateBankTransaction(OrganizationId);
        FakeInvoiceRepository invoices = new();
        invoices.Add(invoice);
        FakeBankTransactionRepository bankTransactions = new();
        bankTransactions.Add(bankTransaction);
        RecordingOrganizationContext organization = new(OrganizationId);
        FakeReviewCaseRepository reviewCases = new();
        ScriptedUnitOfWork unitOfWork = new();
        FixedTimeProvider time = new(ReviewCaseHandlerTestDoubles.OpenedAt);
        OpenReviewCaseHandler handler = CreateHandler(
            organization,
            invoices,
            bankTransactions,
            reviewCases,
            new FakeReviewCaseSnapshotLookup(),
            new FakeReviewCaseOpenConflict(),
            unitOfWork,
            time);

        Result<OpenReviewCaseResponse> result = await handler.Handle(
            new OpenReviewCaseCommand(
                ReviewCaseReason.CompetingMatch,
                invoice.Id,
                bankTransaction.Id,
                " exact-1 ",
                "  first note  "),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.Created);
        Assert.Equal(OrganizationId, organization.OrganizationId);
        Assert.Equal(1, reviewCases.AddCount);
        Assert.Equal(1, unitOfWork.SaveCount);
        Assert.Equal(1, time.ReadCount);
        Assert.Equal("CompetingMatch", result.Value.ReviewCase.Reason);
        Assert.Equal("Open", result.Value.ReviewCase.Status);
        Assert.Equal("exact-1", result.Value.ReviewCase.SourceLabel);
        Assert.Equal("first note", result.Value.ReviewCase.Note);
        Assert.Equal(ReviewCaseHandlerTestDoubles.OpenedAt, result.Value.ReviewCase.OpenedAt);
        Assert.Equal(invoice.Id.Value, result.Value.ReviewCase.InvoiceId);
        Assert.Equal(bankTransaction.Id.Value, result.Value.ReviewCase.BankTransactionId);
        Assert.Equal(
            ReviewCaseConcurrencyToken.Encode(ReviewCaseHandlerTestDoubles.CurrentVersion),
            result.Value.ReviewCase.ConcurrencyToken);
    }

    [Fact]
    public async Task Handle_should_return_the_existing_open_case_without_overwriting_its_note()
    {
        Invoice invoice = ReviewCaseHandlerTestDoubles.CreateInvoice(OrganizationId);
        ReviewCase existing = ReviewCase.Open(
            ReviewCaseId.New(),
            OrganizationId,
            ReviewCaseReason.NeedsInvestigation,
            invoice.Id,
            null,
            ReviewCaseSource.Manual,
            "original",
            ReviewCaseHandlerTestDoubles.OpenedAt);
        FakeInvoiceRepository invoices = new();
        invoices.Add(invoice);
        FakeReviewCaseRepository reviewCases = new();
        FakeReviewCaseSnapshotLookup lookup = new()
        {
            OpenSnapshot = ReviewCaseHandlerTestDoubles.Snapshot(
                existing,
                ReviewCaseHandlerTestDoubles.CurrentVersion),
        };
        ScriptedUnitOfWork unitOfWork = new();
        OpenReviewCaseHandler handler = CreateHandler(
            new RecordingOrganizationContext(OrganizationId),
            invoices,
            reviewCases: reviewCases,
            lookup: lookup,
            unitOfWork: unitOfWork);

        Result<OpenReviewCaseResponse> result = await handler.Handle(
            new OpenReviewCaseCommand(
                ReviewCaseReason.NeedsInvestigation,
                invoice.Id,
                null,
                "smart-1",
                "replacement"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.Created);
        Assert.Equal(existing.Id.Value, result.Value.ReviewCase.ReviewCaseId);
        Assert.Equal("original", result.Value.ReviewCase.Note);
        Assert.Equal("manual", result.Value.ReviewCase.SourceLabel);
        Assert.Equal(0, reviewCases.AddCount);
        Assert.Equal(0, unitOfWork.SaveCount);
    }

    [Fact]
    public async Task Handle_should_create_a_new_case_when_only_terminal_history_exists()
    {
        Invoice invoice = ReviewCaseHandlerTestDoubles.CreateInvoice(OrganizationId);
        ReviewCase historical = ReviewCase.Open(
            ReviewCaseId.New(),
            OrganizationId,
            ReviewCaseReason.NeedsInvestigation,
            invoice.Id,
            null,
            null,
            "old",
            ReviewCaseHandlerTestDoubles.OpenedAt);
        Assert.True(historical.Dismiss("closed", ReviewCaseHandlerTestDoubles.OpenedAt).IsSuccess);
        FakeInvoiceRepository invoices = new();
        invoices.Add(invoice);
        FakeReviewCaseRepository reviewCases = new();
        FakeReviewCaseSnapshotLookup lookup = new()
        {
            OpenSnapshot = null,
            ById = ReviewCaseHandlerTestDoubles.Snapshot(
                historical,
                ReviewCaseHandlerTestDoubles.CurrentVersion),
        };
        OpenReviewCaseHandler handler = CreateHandler(
            new RecordingOrganizationContext(OrganizationId),
            invoices,
            reviewCases: reviewCases,
            lookup: lookup);

        Result<OpenReviewCaseResponse> result = await handler.Handle(
            new OpenReviewCaseCommand(
                ReviewCaseReason.NeedsInvestigation,
                invoice.Id,
                null,
                null,
                "new"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.Created);
        Assert.NotEqual(historical.Id.Value, result.Value.ReviewCase.ReviewCaseId);
        Assert.Equal("new", result.Value.ReviewCase.Note);
        Assert.Equal(1, reviewCases.AddCount);
        Assert.Equal(0, lookup.IdLookups);
    }

    [Fact]
    public async Task Handle_should_return_the_winning_open_case_after_the_filtered_index_conflict()
    {
        Invoice invoice = ReviewCaseHandlerTestDoubles.CreateInvoice(OrganizationId);
        ReviewCase winner = ReviewCase.Open(
            ReviewCaseId.New(),
            OrganizationId,
            ReviewCaseReason.NeedsInvestigation,
            invoice.Id,
            null,
            ReviewCaseSource.Manual,
            "winner",
            ReviewCaseHandlerTestDoubles.OpenedAt);
        FakeInvoiceRepository invoices = new();
        invoices.Add(invoice);
        FakeReviewCaseSnapshotLookup lookup = new();
        ScriptedUnitOfWork unitOfWork = new()
        {
            Exception = new UniqueConstraintConflictException("duplicate"),
        };
        FakeReviewCaseOpenConflict conflict = new() { Conflict = true };
        OpenReviewCaseHandler handler = CreateHandler(
            new RecordingOrganizationContext(OrganizationId),
            invoices,
            lookup: lookup,
            conflict: conflict,
            unitOfWork: unitOfWork);
        lookup.OpenSnapshot = ReviewCaseHandlerTestDoubles.Snapshot(
            winner,
            ReviewCaseHandlerTestDoubles.CurrentVersion);

        Result<OpenReviewCaseResponse> result = await handler.Handle(
            new OpenReviewCaseCommand(
                ReviewCaseReason.NeedsInvestigation,
                invoice.Id,
                null,
                "smart-1",
                "loser"),
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.Created);
        Assert.Equal(winner.Id.Value, result.Value.ReviewCase.ReviewCaseId);
        Assert.Equal("winner", result.Value.ReviewCase.Note);
        Assert.Equal("manual", result.Value.ReviewCase.SourceLabel);
    }

    [Fact]
    public async Task Handle_should_return_a_subject_conflict_when_the_winner_cannot_be_reread()
    {
        Invoice invoice = ReviewCaseHandlerTestDoubles.CreateInvoice(OrganizationId);
        FakeInvoiceRepository invoices = new();
        invoices.Add(invoice);
        ScriptedUnitOfWork unitOfWork = new()
        {
            Exception = new UniqueConstraintConflictException("duplicate"),
        };
        OpenReviewCaseHandler handler = CreateHandler(
            new RecordingOrganizationContext(OrganizationId),
            invoices,
            conflict: new FakeReviewCaseOpenConflict { Conflict = true },
            unitOfWork: unitOfWork);

        Result<OpenReviewCaseResponse> result = await handler.Handle(
            new OpenReviewCaseCommand(
                ReviewCaseReason.NeedsInvestigation,
                invoice.Id,
                null,
                null,
                null),
            CancellationToken.None);

        Assert.Equal(
            ReconciliationApplicationErrors.ReviewCaseSubjectConflict.Code,
            result.Error!.Code);
    }

    [Fact]
    public async Task Handle_should_propagate_a_unique_violation_that_is_not_the_open_subject_index()
    {
        Invoice invoice = ReviewCaseHandlerTestDoubles.CreateInvoice(OrganizationId);
        FakeInvoiceRepository invoices = new();
        invoices.Add(invoice);
        ScriptedUnitOfWork unitOfWork = new()
        {
            Exception = new UniqueConstraintConflictException("primary key"),
        };
        OpenReviewCaseHandler handler = CreateHandler(
            new RecordingOrganizationContext(OrganizationId),
            invoices,
            conflict: new FakeReviewCaseOpenConflict { Conflict = false },
            unitOfWork: unitOfWork);

        UniqueConstraintConflictException exception = await Assert.ThrowsAsync<UniqueConstraintConflictException>(
            () => handler.Handle(
                new OpenReviewCaseCommand(
                    ReviewCaseReason.NeedsInvestigation,
                    invoice.Id,
                    null,
                    null,
                    null),
                CancellationToken.None));

        Assert.Equal("primary key", exception.Message);
    }

    [Fact]
    public async Task Handle_should_not_query_when_the_organization_context_fails()
    {
        FakeInvoiceRepository invoices = new();
        OpenReviewCaseHandler handler = CreateHandler(
            new ThrowingOrganizationContext(),
            invoices);

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.Handle(
            new OpenReviewCaseCommand(
                ReviewCaseReason.NeedsInvestigation,
                InvoiceId.New(),
                null,
                null,
                null),
            CancellationToken.None));

        Assert.Equal(0, invoices.CallCount);
    }

    [Fact]
    public async Task Handle_should_propagate_cancellation_to_the_invoice_lookup()
    {
        FakeInvoiceRepository invoices = new();
        OpenReviewCaseHandler handler = CreateHandler(
            new RecordingOrganizationContext(OrganizationId),
            invoices);
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() => handler.Handle(
            new OpenReviewCaseCommand(
                ReviewCaseReason.NeedsInvestigation,
                InvoiceId.New(),
                null,
                null,
                null),
            cancellation.Token));

        Assert.Equal(cancellation.Token, invoices.CancellationToken);
    }

    [Fact]
    public void Review_case_commands_do_not_depend_on_allocation_confirmation()
    {
        Type[] handlers =
        [
            typeof(OpenReviewCaseHandler),
            typeof(ResolveReviewCaseHandler),
            typeof(DismissReviewCaseHandler),
            typeof(ListReviewCasesHandler),
            typeof(GetReviewCaseHandler),
        ];

        foreach (Type handler in handlers)
        {
            Assert.DoesNotContain(
                handler.GetConstructors().Single().GetParameters(),
                parameter => parameter.ParameterType == typeof(ConfirmExactMatchHandler)
                    || parameter.ParameterType == typeof(ConfirmExplicitAmountHandler)
                    || parameter.ParameterType == typeof(ConfirmAllocationHandler)
                    || parameter.ParameterType.Name.Contains("SmartMatch", StringComparison.Ordinal)
                    || parameter.ParameterType.Name.Contains("ExactMatch", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Handle_should_reject_a_missing_actor_before_reading_the_organization()
    {
        RecordingOrganizationContext organization = new(OrganizationId);
        ScriptedUnitOfWork unitOfWork = new();
        RecordingReviewCaseAuditWriter audit = new();
        OpenReviewCaseHandler handler = CreateHandler(
            organization,
            new FakeInvoiceRepository(),
            unitOfWork: unitOfWork,
            actor: new FixedActorContext(null),
            audit: audit);

        Result<OpenReviewCaseResponse> result = await handler.Handle(
            new OpenReviewCaseCommand(
                ReviewCaseReason.NeedsInvestigation,
                InvoiceId.New(),
                null,
                "manual",
                "note"),
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ReconciliationApplicationErrors.ActorRequired.Code, result.Error.Code);
        Assert.Equal(0, organization.ReadCount);
        Assert.Equal(0, unitOfWork.SaveCount);
        Assert.Empty(audit.Entries);
    }

    [Fact]
    public async Task Handle_should_append_one_opened_entry_for_a_new_case_and_none_for_replay()
    {
        Invoice invoice = ReviewCaseHandlerTestDoubles.CreateInvoice(OrganizationId);
        FakeInvoiceRepository invoices = new();
        invoices.Add(invoice);
        FixedTimeProvider time = new(ReviewCaseHandlerTestDoubles.OpenedAt);
        RecordingReviewCaseAuditWriter audit = new();
        ScriptedUnitOfWork unitOfWork = new();
        OpenReviewCaseHandler created = CreateHandler(
            new RecordingOrganizationContext(OrganizationId),
            invoices,
            unitOfWork: unitOfWork,
            time: time,
            audit: audit);

        Result<OpenReviewCaseResponse> first = await created.Handle(
            new OpenReviewCaseCommand(
                ReviewCaseReason.NeedsInvestigation,
                invoice.Id,
                null,
                "manual",
                " kept "),
            CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.True(first.Value.Created);
        ReviewCaseAuditEntry entry = Assert.Single(audit.Entries);
        Assert.Equal(ReviewCaseAuditAction.Opened, entry.Action);
        Assert.Equal("operator-a", entry.ActorId);
        Assert.Equal(ReviewCaseHandlerTestDoubles.OpenedAt, entry.RecordedAt);
        Assert.Equal("kept", entry.Note);
        Assert.Equal(ReviewCaseSource.Manual, entry.SourceLabel);
        Assert.Equal(1, time.ReadCount);
        Assert.Equal(1, unitOfWork.SaveCount);

        ReviewCase stored = ReviewCase.Open(
            new ReviewCaseId(first.Value.ReviewCase.ReviewCaseId),
            OrganizationId,
            ReviewCaseReason.NeedsInvestigation,
            invoice.Id,
            null,
            ReviewCaseSource.Manual,
            "kept",
            ReviewCaseHandlerTestDoubles.OpenedAt);
        FakeReviewCaseSnapshotLookup lookup = new()
        {
            OpenSnapshot = ReviewCaseHandlerTestDoubles.Snapshot(
                stored,
                ReviewCaseHandlerTestDoubles.CurrentVersion),
        };
        OpenReviewCaseHandler replay = CreateHandler(
            new RecordingOrganizationContext(OrganizationId),
            invoices,
            lookup: lookup,
            unitOfWork: unitOfWork,
            time: time,
            actor: new FixedActorContext("operator-b"),
            audit: audit);

        Result<OpenReviewCaseResponse> second = await replay.Handle(
            new OpenReviewCaseCommand(
                ReviewCaseReason.NeedsInvestigation,
                invoice.Id,
                null,
                "smart-1",
                "other"),
            CancellationToken.None);

        Assert.True(second.IsSuccess);
        Assert.False(second.Value.Created);
        Assert.Single(audit.Entries);
        Assert.Equal("operator-a", audit.Entries[0].ActorId);
        Assert.Equal(1, unitOfWork.SaveCount);
    }

    private static OpenReviewCaseHandler CreateHandler(
        ICurrentOrganizationContext organization,
        FakeInvoiceRepository invoices,
        FakeBankTransactionRepository? bankTransactions = null,
        FakeReviewCaseRepository? reviewCases = null,
        FakeReviewCaseSnapshotLookup? lookup = null,
        FakeReviewCaseOpenConflict? conflict = null,
        ScriptedUnitOfWork? unitOfWork = null,
        TimeProvider? time = null,
        ICurrentActorContext? actor = null,
        RecordingReviewCaseAuditWriter? audit = null)
    {
        return new OpenReviewCaseHandler(
            organization,
            actor ?? new FixedActorContext(),
            invoices,
            bankTransactions ?? new FakeBankTransactionRepository(),
            reviewCases ?? new FakeReviewCaseRepository(),
            lookup ?? new FakeReviewCaseSnapshotLookup(),
            conflict ?? new FakeReviewCaseOpenConflict(),
            audit ?? new RecordingReviewCaseAuditWriter(),
            unitOfWork ?? new ScriptedUnitOfWork(),
            time ?? new FixedTimeProvider(ReviewCaseHandlerTestDoubles.OpenedAt));
    }
}