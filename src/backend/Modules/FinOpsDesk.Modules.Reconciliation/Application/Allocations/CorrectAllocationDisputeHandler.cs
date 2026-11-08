using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Application.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Application.FinancialAudit;
using FinOpsDesk.Modules.Reconciliation.Application.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.FinancialAudit;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;

namespace FinOpsDesk.Modules.Reconciliation.Application.Allocations;

public sealed class CorrectAllocationDisputeHandler
{
    private readonly ICurrentOrganizationContext _currentOrganizationContext;
    private readonly ICurrentActorAttribution _currentActorAttribution;
    private readonly IAllocationDisputeCorrectionReads _reads;
    private readonly IAllocationDisputeCorrectionWork _work;
    private readonly IAllocationDisputeCorrectionReadSignals _signals;
    private readonly IAllocationDisputeCorrectionConflict _conflict;
    private readonly IAllocationRepository _allocationRepository;
    private readonly IInvoiceRepository _invoiceRepository;
    private readonly IBankTransactionRepository _bankTransactionRepository;
    private readonly IFinancialAuditWriter _financialAuditWriter;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    public CorrectAllocationDisputeHandler(
        ICurrentOrganizationContext currentOrganizationContext,
        ICurrentActorAttribution currentActorAttribution,
        IAllocationDisputeCorrectionReads reads,
        IAllocationDisputeCorrectionWork work,
        IAllocationDisputeCorrectionReadSignals signals,
        IAllocationDisputeCorrectionConflict conflict,
        IAllocationRepository allocationRepository,
        IInvoiceRepository invoiceRepository,
        IBankTransactionRepository bankTransactionRepository,
        IFinancialAuditWriter financialAuditWriter,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(currentOrganizationContext);
        ArgumentNullException.ThrowIfNull(currentActorAttribution);
        ArgumentNullException.ThrowIfNull(reads);
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(conflict);
        ArgumentNullException.ThrowIfNull(allocationRepository);
        ArgumentNullException.ThrowIfNull(invoiceRepository);
        ArgumentNullException.ThrowIfNull(bankTransactionRepository);
        ArgumentNullException.ThrowIfNull(financialAuditWriter);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _currentOrganizationContext = currentOrganizationContext;
        _currentActorAttribution = currentActorAttribution;
        _reads = reads;
        _work = work;
        _signals = signals;
        _conflict = conflict;
        _allocationRepository = allocationRepository;
        _invoiceRepository = invoiceRepository;
        _bankTransactionRepository = bankTransactionRepository;
        _financialAuditWriter = financialAuditWriter;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    public async Task<Result<CorrectAllocationDisputeResponse>> Handle(
        CorrectAllocationDisputeCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.IdempotencyKey == Guid.Empty)
        {
            return Result<CorrectAllocationDisputeResponse>.Failure(
                ReconciliationApplicationErrors.AllocationDisputeCorrectionIdempotencyKeyInvalid);
        }

        if (!_currentActorAttribution.TryGet(out ReviewCaseActorSource actorSource, out string actorId))
        {
            return Result<CorrectAllocationDisputeResponse>.Failure(
                ReconciliationApplicationErrors.ActorRequired);
        }

        if (!AllocationDisputeCorrection.TryNormalizeReason(
                command.Reason,
                out string reason,
                out AllocationDisputeCorrectionReasonIssue issue))
        {
            return Result<CorrectAllocationDisputeResponse>.Failure(ReasonError(issue));
        }

        OrganizationId organizationId = _currentOrganizationContext.OrganizationId;
        Result<CorrectAllocationDisputeResponse>? keyed = await ClassifyKeyAsync(
            _reads,
            organizationId,
            command.DisputeId,
            command.IdempotencyKey,
            reason,
            string.Empty,
            cancellationToken);
        if (keyed is not null)
        {
            return keyed;
        }

        AllocationDisputeTarget? dispute = await _reads.FindDisputeAsync(
            organizationId,
            command.DisputeId,
            cancellationToken);
        if (dispute is null)
        {
            return Result<CorrectAllocationDisputeResponse>.Failure(
                ReconciliationApplicationErrors.AllocationDisputeNotFound);
        }

        Result<CorrectAllocationDisputeResponse>? decision = await RequireUpheldAsync(
            _reads,
            organizationId,
            command.DisputeId,
            cancellationToken);
        if (decision is not null)
        {
            return decision;
        }

        Result<CorrectAllocationDisputeResponse>? observed = await ClassifyDisputeAndStatusAsync(
            _reads,
            organizationId,
            command.DisputeId,
            dispute.AllocationId,
            command.IdempotencyKey,
            reason,
            string.Empty,
            cancellationToken);
        if (observed is not null)
        {
            return observed;
        }

        await using IAllocationDisputeCorrectionSession session = await _work.BeginAsync(
            organizationId,
            dispute.AllocationId,
            cancellationToken);
        try
        {
            if (!session.AllocationFound)
            {
                await EndAsync(session);
                return Result<CorrectAllocationDisputeResponse>.Failure(
                    ReconciliationApplicationErrors.AllocationDisputeCorrectionIntegrityConflict);
            }

            AllocationDisputeTarget? lockedDispute = await session.FindDisputeAsync(
                organizationId,
                command.DisputeId,
                cancellationToken);
            if (lockedDispute is null)
            {
                await EndAsync(session);
                return Result<CorrectAllocationDisputeResponse>.Failure(
                    ReconciliationApplicationErrors.AllocationDisputeNotFound);
            }

            if (lockedDispute.AllocationId != dispute.AllocationId)
            {
                await EndAsync(session);
                return Result<CorrectAllocationDisputeResponse>.Failure(
                    ReconciliationApplicationErrors.AllocationDisputeCorrectionConcurrencyConflict);
            }

            Result<CorrectAllocationDisputeResponse>? lockedDecision = await RequireUpheldAsync(
                session,
                organizationId,
                command.DisputeId,
                cancellationToken);
            if (lockedDecision is not null)
            {
                await EndAsync(session);
                return lockedDecision;
            }

            Result<CorrectAllocationDisputeResponse>? locked = await ClassifyAllAsync(
                session,
                organizationId,
                command.DisputeId,
                lockedDispute.AllocationId,
                command.IdempotencyKey,
                reason,
                "locked-",
                cancellationToken);
            if (locked is not null)
            {
                await EndAsync(session);
                return locked;
            }

            Allocation? allocation = await _allocationRepository.GetByIdAsync(
                organizationId,
                lockedDispute.AllocationId,
                cancellationToken);
            if (allocation is null)
            {
                await EndAsync(session);
                return Result<CorrectAllocationDisputeResponse>.Failure(
                    ReconciliationApplicationErrors.AllocationDisputeCorrectionIntegrityConflict);
            }

            Invoice? invoice = await _invoiceRepository.GetByIdAsync(
                organizationId,
                allocation.InvoiceId,
                cancellationToken);
            BankTransaction? bankTransaction = await _bankTransactionRepository.GetByIdAsync(
                organizationId,
                allocation.BankTransactionId,
                cancellationToken);
            if (invoice is null || bankTransaction is null)
            {
                await EndAsync(session);
                return Result<CorrectAllocationDisputeResponse>.Failure(
                    ReconciliationApplicationErrors.AllocationDisputeCorrectionIntegrityConflict);
            }

            DateTimeOffset correctedAt = _timeProvider.GetUtcNow();
            Result applied;
            try
            {
                applied = AllocationReversal.Apply(allocation, invoice, bankTransaction, reason, correctedAt);
            }
            catch (ArgumentException)
            {
                await EndAsync(session);
                return Result<CorrectAllocationDisputeResponse>.Failure(
                    ReconciliationApplicationErrors.AllocationDisputeCorrectionIntegrityConflict);
            }

            if (applied.IsFailure)
            {
                await EndAsync(session);
                return Result<CorrectAllocationDisputeResponse>.Failure(applied.Error);
            }

            AllocationDisputeCorrection correction = AllocationDisputeCorrection.Create(
                organizationId,
                command.DisputeId,
                allocation.Id,
                reason,
                actorSource,
                actorId,
                correctedAt,
                command.IdempotencyKey);
            _allocationRepository.AssignReversalIdempotencyKey(allocation, command.IdempotencyKey);
            _financialAuditWriter.AppendReversal(allocation, actorId, command.IdempotencyKey);
            session.Add(correction);

            try
            {
                await _unitOfWork.SaveChangesAsync(cancellationToken);
            }
            catch (UniqueConstraintConflictException exception)
                when (_conflict.IsRecognizedUniqueConflict(exception))
            {
                await EndAsync(session);
                return await RecoverAsync(
                    organizationId,
                    command.DisputeId,
                    command.IdempotencyKey,
                    reason,
                    cancellationToken);
            }
            catch (ConcurrencyConflictException)
            {
                await EndAsync(session);
                return await RecoverOrConcurrencyAsync(
                    organizationId,
                    command.DisputeId,
                    command.IdempotencyKey,
                    reason,
                    cancellationToken);
            }
            catch (DbUpdateException exception) when (_conflict.IsDisputeOrAllocationForeignKey(exception))
            {
                await EndAsync(session);
                return await RecoverForeignKeyAsync(
                    organizationId,
                    command.DisputeId,
                    command.IdempotencyKey,
                    reason,
                    cancellationToken);
            }

            try
            {
                await session.CommitAsync(cancellationToken);
            }
            catch
            {
                await EndAsync(session);
                throw;
            }

            return Result<CorrectAllocationDisputeResponse>.Success(
                ToResponse(
                    command.DisputeId,
                    allocation.Id,
                    allocation.InvoiceId,
                    allocation.BankTransactionId,
                    allocation.Amount,
                    correctedAt,
                    reason,
                    actorSource,
                    actorId,
                    replayed: false));
        }
        catch
        {
            await EndAsync(session);
            throw;
        }
    }

    private async Task<Result<CorrectAllocationDisputeResponse>> RecoverAsync(
        OrganizationId organizationId,
        AllocationDisputeId disputeId,
        Guid idempotencyKey,
        string reason,
        CancellationToken cancellationToken)
    {
        Result<CorrectAllocationDisputeResponse>? classified = await ClassifyAllAsync(
            _reads,
            organizationId,
            disputeId,
            allocationId: null,
            idempotencyKey,
            reason,
            "recovery-",
            cancellationToken);
        if (classified is not null)
        {
            return classified;
        }

        AllocationDisputeTarget? dispute = await _reads.FindDisputeAsync(
            organizationId,
            disputeId,
            cancellationToken);
        if (dispute is null)
        {
            return Result<CorrectAllocationDisputeResponse>.Failure(
                ReconciliationApplicationErrors.AllocationDisputeNotFound);
        }

        Result<CorrectAllocationDisputeResponse>? decision = await RequireUpheldAsync(
            _reads,
            organizationId,
            disputeId,
            cancellationToken);
        if (decision is not null)
        {
            return decision;
        }

        Result<CorrectAllocationDisputeResponse>? rest = await ClassifyDisputeAndStatusAsync(
            _reads,
            organizationId,
            disputeId,
            dispute.AllocationId,
            idempotencyKey,
            reason,
            "recovery-",
            cancellationToken);
        return rest ?? Result<CorrectAllocationDisputeResponse>.Failure(
            ReconciliationApplicationErrors.AllocationDisputeCorrectionConcurrencyConflict);
    }

    private async Task<Result<CorrectAllocationDisputeResponse>> RecoverOrConcurrencyAsync(
        OrganizationId organizationId,
        AllocationDisputeId disputeId,
        Guid idempotencyKey,
        string reason,
        CancellationToken cancellationToken)
    {
        Result<CorrectAllocationDisputeResponse> recovered = await RecoverAsync(
            organizationId,
            disputeId,
            idempotencyKey,
            reason,
            cancellationToken);
        if (recovered.IsFailure
            && recovered.Error.Code == ReconciliationApplicationErrors.AllocationDisputeCorrectionConcurrencyConflict.Code)
        {
            return Result<CorrectAllocationDisputeResponse>.Failure(
                ReconciliationApplicationErrors.ConcurrencyConflict);
        }

        return recovered;
    }

    private async Task<Result<CorrectAllocationDisputeResponse>> RecoverForeignKeyAsync(
        OrganizationId organizationId,
        AllocationDisputeId disputeId,
        Guid idempotencyKey,
        string reason,
        CancellationToken cancellationToken)
    {
        Result<CorrectAllocationDisputeResponse>? keyed = await ClassifyKeyAsync(
            _reads,
            organizationId,
            disputeId,
            idempotencyKey,
            reason,
            "recovery-",
            cancellationToken);
        if (keyed is not null)
        {
            return keyed;
        }

        if (await _reads.FindDisputeAsync(organizationId, disputeId, cancellationToken) is null)
        {
            return Result<CorrectAllocationDisputeResponse>.Failure(
                ReconciliationApplicationErrors.AllocationDisputeNotFound);
        }

        return Result<CorrectAllocationDisputeResponse>.Failure(
            ReconciliationApplicationErrors.AllocationDisputeCorrectionConcurrencyConflict);
    }

    private async Task<Result<CorrectAllocationDisputeResponse>?> ClassifyAllAsync(
        IAllocationDisputeCorrectionReads source,
        OrganizationId organizationId,
        AllocationDisputeId disputeId,
        AllocationId? allocationId,
        Guid idempotencyKey,
        string reason,
        string prefix,
        CancellationToken cancellationToken)
    {
        Result<CorrectAllocationDisputeResponse>? keyed = await ClassifyKeyAsync(
            source,
            organizationId,
            disputeId,
            idempotencyKey,
            reason,
            prefix,
            cancellationToken);
        if (keyed is not null || allocationId is null)
        {
            return keyed;
        }

        return await ClassifyDisputeAndStatusAsync(
            source,
            organizationId,
            disputeId,
            allocationId,
            idempotencyKey,
            reason,
            prefix,
            cancellationToken);
    }

    private async Task<Result<CorrectAllocationDisputeResponse>?> ClassifyKeyAsync(
        IAllocationDisputeCorrectionReads source,
        OrganizationId organizationId,
        AllocationDisputeId disputeId,
        Guid idempotencyKey,
        string reason,
        string prefix,
        CancellationToken cancellationToken)
    {
        AllocationDisputeCorrectionLink? byKey = await source.FindCorrectionByKeyAsync(
            organizationId,
            idempotencyKey,
            cancellationToken);
        await _signals.OnReadAsync(prefix + "correction-by-key", cancellationToken);
        AllocationReversalFacts? reversal = await source.FindReversalByKeyAsync(
            organizationId,
            idempotencyKey,
            cancellationToken);
        await _signals.OnReadAsync(prefix + "reversal-by-key", cancellationToken);
        if (reversal is not null && byKey is null)
        {
            byKey = await source.FindCorrectionByKeyAsync(organizationId, idempotencyKey, cancellationToken);
            await _signals.OnReadAsync(prefix + "correction-by-key-reread", cancellationToken);
        }

        if (byKey is not null)
        {
            return await MatchLinkAsync(source, organizationId, byKey, disputeId, reason, cancellationToken);
        }

        if (reversal is not null)
        {
            return Result<CorrectAllocationDisputeResponse>.Failure(
                ReconciliationApplicationErrors.AllocationDisputeCorrectionIdempotencyPayloadConflict);
        }

        return null;
    }

    private async Task<Result<CorrectAllocationDisputeResponse>?> ClassifyDisputeAndStatusAsync(
        IAllocationDisputeCorrectionReads source,
        OrganizationId organizationId,
        AllocationDisputeId disputeId,
        AllocationId allocationId,
        Guid idempotencyKey,
        string reason,
        string prefix,
        CancellationToken cancellationToken)
    {
        Result<CorrectAllocationDisputeResponse>? dispute = await ClassifyDisputeRowAsync(
            source,
            organizationId,
            disputeId,
            idempotencyKey,
            reason,
            prefix,
            cancellationToken);
        if (dispute is not null)
        {
            return dispute;
        }

        AllocationReversalFacts? allocation = await source.FindAllocationAsync(
            organizationId,
            allocationId,
            cancellationToken);
        await _signals.OnReadAsync(prefix + "allocation-status", cancellationToken);
        if (allocation is null)
        {
            return Result<CorrectAllocationDisputeResponse>.Failure(
                ReconciliationApplicationErrors.AllocationDisputeCorrectionIntegrityConflict);
        }

        if (allocation.Status != AllocationStatus.Reversed)
        {
            return allocation.Status == AllocationStatus.Confirmed
                ? null
                : Result<CorrectAllocationDisputeResponse>.Failure(
                    ReconciliationApplicationErrors.AllocationDisputeCorrectionIntegrityConflict);
        }

        Result<CorrectAllocationDisputeResponse>? closingKey = await ClassifyKeyAsync(
            source,
            organizationId,
            disputeId,
            idempotencyKey,
            reason,
            prefix + "closing-",
            cancellationToken);
        if (closingKey is not null)
        {
            return closingKey;
        }

        Result<CorrectAllocationDisputeResponse>? closingDispute = await ClassifyDisputeRowAsync(
            source,
            organizationId,
            disputeId,
            idempotencyKey,
            reason,
            prefix + "closing-",
            cancellationToken);
        if (closingDispute is not null)
        {
            return closingDispute;
        }

        return Result<CorrectAllocationDisputeResponse>.Failure(ReconciliationErrors.AllocationAlreadyReversed);
    }

    private async Task<Result<CorrectAllocationDisputeResponse>?> ClassifyDisputeRowAsync(
        IAllocationDisputeCorrectionReads source,
        OrganizationId organizationId,
        AllocationDisputeId disputeId,
        Guid idempotencyKey,
        string reason,
        string prefix,
        CancellationToken cancellationToken)
    {
        AllocationDisputeCorrectionLink? stored = await source.FindCorrectionByDisputeAsync(
            organizationId,
            disputeId,
            cancellationToken);
        await _signals.OnReadAsync(prefix + "correction-by-dispute", cancellationToken);
        if (stored is null)
        {
            return null;
        }

        if (stored.IdempotencyKey == idempotencyKey)
        {
            return await MatchLinkAsync(source, organizationId, stored, disputeId, reason, cancellationToken);
        }

        return Result<CorrectAllocationDisputeResponse>.Failure(
            ReconciliationApplicationErrors.AllocationDisputeCorrectionAlreadyCorrected);
    }

    private async Task<Result<CorrectAllocationDisputeResponse>> MatchLinkAsync(
        IAllocationDisputeCorrectionReads source,
        OrganizationId organizationId,
        AllocationDisputeCorrectionLink link,
        AllocationDisputeId disputeId,
        string reason,
        CancellationToken cancellationToken)
    {
        if (link.DisputeId != disputeId
            || !string.Equals(link.Reason, reason, StringComparison.Ordinal))
        {
            return Result<CorrectAllocationDisputeResponse>.Failure(
                ReconciliationApplicationErrors.AllocationDisputeCorrectionIdempotencyPayloadConflict);
        }

        AllocationDisputeTarget? dispute = await source.FindDisputeAsync(
            organizationId,
            disputeId,
            cancellationToken);
        AllocationReversalFacts? allocation = await source.FindAllocationAsync(
            organizationId,
            link.AllocationId,
            cancellationToken);
        IReadOnlyList<AllocationReversalAuditFact> audits = await source.ListReversalAuditsAsync(
            organizationId,
            link.AllocationId,
            cancellationToken);
        if (dispute is null
            || dispute.AllocationId != link.AllocationId
            || allocation is null
            || !EvidenceAgrees(link, allocation, audits))
        {
            return Result<CorrectAllocationDisputeResponse>.Failure(
                ReconciliationApplicationErrors.AllocationDisputeCorrectionIntegrityConflict);
        }

        return Result<CorrectAllocationDisputeResponse>.Success(
            ToResponse(
                link.DisputeId,
                allocation.AllocationId,
                allocation.InvoiceId,
                allocation.BankTransactionId,
                allocation.Amount,
                link.CorrectedAt,
                link.Reason,
                link.ActorSource,
                link.ActorId,
                replayed: true));
    }

    private static bool EvidenceAgrees(
        AllocationDisputeCorrectionLink link,
        AllocationReversalFacts? allocation,
        IReadOnlyList<AllocationReversalAuditFact> audits)
    {
        if (allocation is null
            || allocation.Status != AllocationStatus.Reversed
            || allocation.ReversalIdempotencyKey != link.IdempotencyKey
            || allocation.ReversedAt != link.CorrectedAt
            || !string.Equals(allocation.ReversalReason, link.Reason, StringComparison.Ordinal)
            || audits.Count != 1)
        {
            return false;
        }

        AllocationReversalAuditFact audit = audits[0];
        return audit.AllocationId == allocation.AllocationId
            && audit.InvoiceId == allocation.InvoiceId
            && audit.BankTransactionId == allocation.BankTransactionId
            && audit.Action == FinancialAuditAction.Reversal
            && audit.Operation == AllocationIdempotencyOperation.Reversal
            && audit.IdempotencyKey == link.IdempotencyKey
            && audit.Amount.Amount == allocation.Amount.Amount
            && audit.Amount.Currency == allocation.Amount.Currency
            && audit.RecordedAt == allocation.ReversedAt
            && string.Equals(audit.Reason, link.Reason, StringComparison.Ordinal)
            && string.Equals(audit.ActorId, link.ActorId, StringComparison.Ordinal);
    }

    private static async Task<Result<CorrectAllocationDisputeResponse>?> RequireUpheldAsync(
        IAllocationDisputeCorrectionReads source,
        OrganizationId organizationId,
        AllocationDisputeId disputeId,
        CancellationToken cancellationToken)
    {
        AllocationDisputeResolutionOutcome? outcome = await source.FindResolutionOutcomeAsync(
            organizationId,
            disputeId,
            cancellationToken);
        if (outcome is null)
        {
            return Result<CorrectAllocationDisputeResponse>.Failure(
                ReconciliationApplicationErrors.AllocationDisputeCorrectionDecisionRequired);
        }

        if (outcome != AllocationDisputeResolutionOutcome.Upheld)
        {
            return Result<CorrectAllocationDisputeResponse>.Failure(
                ReconciliationApplicationErrors.AllocationDisputeCorrectionNotUpheld);
        }

        return null;
    }

    private static async Task EndAsync(IAllocationDisputeCorrectionSession session)
    {
        await session.RollbackAsync(CancellationToken.None);
    }

    private static CorrectAllocationDisputeResponse ToResponse(
        AllocationDisputeId disputeId,
        AllocationId allocationId,
        InvoiceId invoiceId,
        BankTransactionId bankTransactionId,
        Money amount,
        DateTimeOffset reversedAt,
        string reason,
        ReviewCaseActorSource actorSource,
        string actorId,
        bool replayed)
    {
        return new CorrectAllocationDisputeResponse(
            disputeId.Value,
            allocationId.Value,
            invoiceId.Value,
            bankTransactionId.Value,
            "reversed",
            new MoneyDto(amount.Amount, amount.Currency.Value),
            reversedAt,
            reason,
            AllocationDispute.ActorSourceName(actorSource),
            actorId,
            replayed);
    }

    private static Error ReasonError(AllocationDisputeCorrectionReasonIssue issue)
    {
        return issue == AllocationDisputeCorrectionReasonIssue.TooLong
            ? ReconciliationApplicationErrors.AllocationDisputeCorrectionReasonTooLong
            : ReconciliationApplicationErrors.AllocationDisputeCorrectionReasonRequired;
    }
}