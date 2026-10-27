using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Application.Allocations;
using FinOpsDesk.Modules.Reconciliation.Application.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Application.BatchExecutions;
using FinOpsDesk.Modules.Reconciliation.Application.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.BatchExecutions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.Matching;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Configurations;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Repositories;

internal sealed class BatchExactMatchItemExecutor : IBatchExactMatchItemExecutor
{
    private readonly ReconciliationDbContext _dbContext;
    private readonly IInvoiceRepository _invoiceRepository;
    private readonly IBankTransactionRepository _bankTransactionRepository;
    private readonly IAllocationRepository _allocationRepository;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICompetitorProbeQueries _competitorProbeQueries;
    private readonly IOpenReviewBlockQueries _openReviewBlockQueries;
    private readonly IAllocationIdempotencyLookup _idempotencyLookup;
    private readonly IBatchExactMatchItemSynchronization _synchronization;

    public BatchExactMatchItemExecutor(
        ReconciliationDbContext dbContext,
        IInvoiceRepository invoiceRepository,
        IBankTransactionRepository bankTransactionRepository,
        IAllocationRepository allocationRepository,
        IUnitOfWork unitOfWork,
        ICompetitorProbeQueries competitorProbeQueries,
        IOpenReviewBlockQueries openReviewBlockQueries,
        IAllocationIdempotencyLookup idempotencyLookup,
        IBatchExactMatchItemSynchronization synchronization)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(invoiceRepository);
        ArgumentNullException.ThrowIfNull(bankTransactionRepository);
        ArgumentNullException.ThrowIfNull(allocationRepository);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(competitorProbeQueries);
        ArgumentNullException.ThrowIfNull(openReviewBlockQueries);
        ArgumentNullException.ThrowIfNull(idempotencyLookup);
        ArgumentNullException.ThrowIfNull(synchronization);

        _dbContext = dbContext;
        _invoiceRepository = invoiceRepository;
        _bankTransactionRepository = bankTransactionRepository;
        _allocationRepository = allocationRepository;
        _unitOfWork = unitOfWork;
        _competitorProbeQueries = competitorProbeQueries;
        _openReviewBlockQueries = openReviewBlockQueries;
        _idempotencyLookup = idempotencyLookup;
        _synchronization = synchronization;
    }

    public async Task<BatchItemAttempt> ExecuteAsync(
        OrganizationId organizationId,
        Guid batchIdempotencyKey,
        InvoiceId invoiceId,
        BankTransactionId bankTransactionId,
        DateTimeOffset confirmedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(invoiceId);
        ArgumentNullException.ThrowIfNull(bankTransactionId);

        Guid itemKey = BatchExactMatchItemKey.Derive(
            organizationId,
            batchIdempotencyKey,
            invoiceId,
            bankTransactionId);

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            Allocation? existing = await _dbContext.Allocations
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    allocation => allocation.OrganizationId == organizationId
                        && allocation.IdempotencyKey == itemKey,
                    cancellationToken);

            if (existing is not null)
            {
                BatchItemAttempt recovered = ClassifyExisting(
                    existing,
                    organizationId,
                    invoiceId,
                    bankTransactionId);

                if (recovered is BatchItemAttempt.Stopped)
                {
                    return recovered;
                }

                await transaction.CommitAsync(cancellationToken);
                return recovered;
            }

            Invoice? invoice = await _invoiceRepository.GetByIdAsync(
                organizationId,
                invoiceId,
                cancellationToken);
            BankTransaction? bankTransaction = await _bankTransactionRepository.GetByIdAsync(
                organizationId,
                bankTransactionId,
                cancellationToken);

            if (invoice is null || bankTransaction is null)
            {
                return new BatchItemAttempt.Rejected(BatchExecutionItemOutcome.NotFound);
            }

            Result<Money> amount = ExactMatchConfirmation.TryDeriveAmount(
                invoice.InvoiceNumber,
                bankTransaction.Reference,
                invoice.OutstandingAmount,
                bankTransaction.UnappliedAmount);

            if (amount.IsFailure)
            {
                return new BatchItemAttempt.Rejected(MapEligibility(amount.Error));
            }

            BatchItemAttempt? probe = await ClassifyProbeAsync(
                organizationId,
                invoice,
                bankTransaction,
                amount.Value,
                cancellationToken);

            if (probe is not null)
            {
                return probe;
            }

            IReadOnlyList<ReviewBlockSubject> subjects = await _openReviewBlockQueries.ListOpenForPairAsync(
                organizationId,
                invoiceId,
                bankTransactionId,
                cancellationToken);
            OpenReviewBlockSet blocks = OpenReviewBlocks.Collect(subjects);

            if (blocks.InvoiceIds.Contains(invoiceId)
                || blocks.BankTransactionIds.Contains(bankTransactionId))
            {
                return new BatchItemAttempt.Rejected(BatchExecutionItemOutcome.SkippedReview);
            }

            Result applied = AllocationConfirmation.Apply(invoice, bankTransaction, amount.Value);

            if (applied.IsFailure)
            {
                return new BatchItemAttempt.Rejected(MapEligibility(applied.Error));
            }

            var allocation = new Allocation(
                AllocationId.New(),
                organizationId,
                invoice.Id,
                bankTransaction.Id,
                amount.Value,
                confirmedAt,
                itemKey,
                AllocationIdempotencyOperation.BatchExactMatch);

            await _synchronization.OnItemReadyToCommitAsync(itemKey, cancellationToken);
            _allocationRepository.Add(allocation);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new BatchItemAttempt.Confirmed(allocation.Id, allocation.Amount);
        }
        catch (ConcurrencyConflictException)
        {
            return new BatchItemAttempt.Rejected(BatchExecutionItemOutcome.Conflict);
        }
        catch (UniqueConstraintConflictException exception)
            when (SqlServerUniqueIndexes.IsViolation(
                exception,
                AllocationConfiguration.IdempotencyIndexName))
        {
            return await RecoverCommittedAsync(
                organizationId,
                itemKey,
                invoiceId,
                bankTransactionId,
                cancellationToken);
        }
    }

    private async Task<BatchItemAttempt?> ClassifyProbeAsync(
        OrganizationId organizationId,
        Invoice invoice,
        BankTransaction bankTransaction,
        Money amount,
        CancellationToken cancellationToken)
    {
        CompetitorProbeQueryResult invoices = await _competitorProbeQueries.ProbeInvoicesAsync(
            organizationId,
            invoice.Id.Value,
            amount.Currency.Value,
            amount.Amount,
            cancellationToken);

        if (invoices.InconsistentMoney)
        {
            return new BatchItemAttempt.Stopped(ReconciliationApplicationErrors.InconsistentMatchMoney);
        }

        CompetitorProbeQueryResult bankTransactions = await _competitorProbeQueries.ProbeBankTransactionsAsync(
            organizationId,
            bankTransaction.Id.Value,
            amount.Currency.Value,
            amount.Amount,
            cancellationToken);

        if (bankTransactions.InconsistentMoney)
        {
            return new BatchItemAttempt.Stopped(ReconciliationApplicationErrors.InconsistentMatchMoney);
        }

        CompetitorProbeOutcome invoiceOutcome = CompetitorProbeRules.Evaluate(
            invoice.InvoiceNumber,
            invoices.Rows,
            invoices.Truncated);
        CompetitorProbeOutcome bankOutcome = CompetitorProbeRules.Evaluate(
            bankTransaction.Reference,
            bankTransactions.Rows,
            bankTransactions.Truncated);

        if (invoiceOutcome == CompetitorProbeOutcome.Ambiguous
            || bankOutcome == CompetitorProbeOutcome.Ambiguous)
        {
            return new BatchItemAttempt.Rejected(BatchExecutionItemOutcome.SkippedAmbiguous);
        }

        if (invoiceOutcome == CompetitorProbeOutcome.CoverageLimited
            || bankOutcome == CompetitorProbeOutcome.CoverageLimited)
        {
            return new BatchItemAttempt.Rejected(BatchExecutionItemOutcome.SkippedCoverage);
        }

        return null;
    }

    private async Task<BatchItemAttempt> RecoverCommittedAsync(
        OrganizationId organizationId,
        Guid itemKey,
        InvoiceId invoiceId,
        BankTransactionId bankTransactionId,
        CancellationToken cancellationToken)
    {
        ConfirmedAllocationRecord? winner = await _idempotencyLookup.FindAsync(
            organizationId,
            itemKey,
            cancellationToken);

        if (winner is null)
        {
            return new BatchItemAttempt.Stopped(ReconciliationApplicationErrors.BatchInfrastructureFailure);
        }

        if (winner.IdempotencyOperation != AllocationIdempotencyOperation.BatchExactMatch
            || winner.InvoiceId != invoiceId
            || winner.BankTransactionId != bankTransactionId)
        {
            return new BatchItemAttempt.Stopped(
                ReconciliationApplicationErrors.BatchAllocationIntegrityConflict);
        }

        return new BatchItemAttempt.Confirmed(winner.AllocationId, winner.Amount);
    }

    private static BatchItemAttempt ClassifyExisting(
        Allocation existing,
        OrganizationId organizationId,
        InvoiceId invoiceId,
        BankTransactionId bankTransactionId)
    {
        if (existing.OrganizationId != organizationId
            || existing.IdempotencyOperation != AllocationIdempotencyOperation.BatchExactMatch
            || existing.InvoiceId != invoiceId
            || existing.BankTransactionId != bankTransactionId)
        {
            return new BatchItemAttempt.Stopped(
                ReconciliationApplicationErrors.BatchAllocationIntegrityConflict);
        }

        return new BatchItemAttempt.Confirmed(existing.Id, existing.Amount);
    }

    private static BatchExecutionItemOutcome MapEligibility(Error error)
    {
        if (error == ReconciliationErrors.MatchReferenceMismatch
            || error == ReconciliationErrors.MatchCurrencyMismatch
            || error == ReconciliationErrors.MatchStale
            || error == ReconciliationErrors.CurrencyMismatch
            || error == ReconciliationErrors.InvoiceAllocationExceedsOutstanding
            || error == ReconciliationErrors.BankTransactionAllocationExceedsUnapplied)
        {
            return BatchExecutionItemOutcome.Stale;
        }

        throw new InvalidOperationException(
            "Batch item eligibility returned an unexpected domain error: " + error.Code);
    }
}