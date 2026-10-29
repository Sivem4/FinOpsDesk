using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Application.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Application.FinancialAudit;
using FinOpsDesk.Modules.Reconciliation.Application.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.FinancialAudit;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Application.Allocations;

public sealed class ReverseAllocationHandler
{
    private readonly ICurrentOrganizationContext _currentOrganizationContext;
    private readonly ICurrentActorContext _currentActorContext;
    private readonly IAllocationRepository _allocationRepository;
    private readonly IInvoiceRepository _invoiceRepository;
    private readonly IBankTransactionRepository _bankTransactionRepository;
    private readonly IAllocationReversalLookup _reversalLookup;
    private readonly IAllocationReversalIdempotencyConflict _reversalIdempotencyConflict;
    private readonly IFinancialAuditWriter _financialAuditWriter;
    private readonly IUnitOfWork _unitOfWork;
    private readonly TimeProvider _timeProvider;

    public ReverseAllocationHandler(
        ICurrentOrganizationContext currentOrganizationContext,
        ICurrentActorContext currentActorContext,
        IAllocationRepository allocationRepository,
        IInvoiceRepository invoiceRepository,
        IBankTransactionRepository bankTransactionRepository,
        IAllocationReversalLookup reversalLookup,
        IAllocationReversalIdempotencyConflict reversalIdempotencyConflict,
        IFinancialAuditWriter financialAuditWriter,
        IUnitOfWork unitOfWork,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(currentOrganizationContext);
        ArgumentNullException.ThrowIfNull(currentActorContext);
        ArgumentNullException.ThrowIfNull(allocationRepository);
        ArgumentNullException.ThrowIfNull(invoiceRepository);
        ArgumentNullException.ThrowIfNull(bankTransactionRepository);
        ArgumentNullException.ThrowIfNull(reversalLookup);
        ArgumentNullException.ThrowIfNull(reversalIdempotencyConflict);
        ArgumentNullException.ThrowIfNull(financialAuditWriter);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _currentOrganizationContext = currentOrganizationContext;
        _currentActorContext = currentActorContext;
        _allocationRepository = allocationRepository;
        _invoiceRepository = invoiceRepository;
        _bankTransactionRepository = bankTransactionRepository;
        _reversalLookup = reversalLookup;
        _reversalIdempotencyConflict = reversalIdempotencyConflict;
        _financialAuditWriter = financialAuditWriter;
        _unitOfWork = unitOfWork;
        _timeProvider = timeProvider;
    }

    public async Task<Result<ReverseAllocationResponse>> Handle(
        ReverseAllocationCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!_currentActorContext.TryGetActorId(out string actorId))
        {
            return Result<ReverseAllocationResponse>.Failure(
                ReconciliationApplicationErrors.ActorRequired);
        }

        if (command.IdempotencyKey == Guid.Empty)
        {
            return Result<ReverseAllocationResponse>.Failure(
                ReconciliationApplicationErrors.IdempotencyKeyInvalid);
        }

        if (string.IsNullOrWhiteSpace(command.Reason))
        {
            return Result<ReverseAllocationResponse>.Failure(
                ReconciliationApplicationErrors.AllocationReversalInvalidRequest);
        }

        string reason = command.Reason.Trim();
        if (reason.Length > Allocation.MaxReversalReasonLength)
        {
            return Result<ReverseAllocationResponse>.Failure(
                ReconciliationErrors.AllocationReversalReasonTooLong);
        }

        OrganizationId organizationId = _currentOrganizationContext.OrganizationId;
        AllocationReversalKeyRecord? existing = await _reversalLookup.FindByKeyAsync(
            organizationId,
            command.IdempotencyKey,
            cancellationToken);

        if (existing is not null)
        {
            return Classify(existing, command.AllocationId, reason);
        }

        Allocation? allocation = await _allocationRepository.GetByIdAsync(
            organizationId,
            command.AllocationId,
            cancellationToken);

        if (allocation is null)
        {
            return Result<ReverseAllocationResponse>.Failure(
                ReconciliationApplicationErrors.AllocationNotFound);
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
            return Result<ReverseAllocationResponse>.Failure(
                ReconciliationApplicationErrors.AllocationReversalIntegrityConflict);
        }

        Result applied;
        try
        {
            applied = AllocationReversal.Apply(
                allocation,
                invoice,
                bankTransaction,
                reason,
                _timeProvider.GetUtcNow());
        }
        catch (ArgumentException)
        {
            return Result<ReverseAllocationResponse>.Failure(
                ReconciliationApplicationErrors.AllocationReversalIntegrityConflict);
        }

        if (applied.IsFailure)
        {
            return Result<ReverseAllocationResponse>.Failure(applied.Error);
        }

        _allocationRepository.AssignReversalIdempotencyKey(allocation, command.IdempotencyKey);
        _financialAuditWriter.AppendReversal(allocation, actorId, command.IdempotencyKey);

        try
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyConflictException)
        {
            return await RecoverAsync(
                organizationId,
                command.AllocationId,
                command.IdempotencyKey,
                reason,
                cancellationToken);
        }
        catch (UniqueConstraintConflictException exception)
            when (_reversalIdempotencyConflict.IsReversalIdempotencyIndexViolation(exception))
        {
            return await RecoverAsync(
                organizationId,
                command.AllocationId,
                command.IdempotencyKey,
                reason,
                cancellationToken);
        }
        catch (UniqueConstraintConflictException)
        {
            return await RecoverAsync(
                organizationId,
                command.AllocationId,
                command.IdempotencyKey,
                reason,
                cancellationToken);
        }

        if (allocation.ReversedAt is not DateTimeOffset reversedAt
            || allocation.ReversalReason is not string storedReason)
        {
            throw new InvalidOperationException(
                "A successful reversal did not retain its reversal time and reason.");
        }

        return Result<ReverseAllocationResponse>.Success(
            ToResponse(allocation.Id, allocation.Amount, reversedAt, storedReason, replayed: false));
    }

    private async Task<Result<ReverseAllocationResponse>> RecoverAsync(
        OrganizationId organizationId,
        AllocationId allocationId,
        Guid reversalIdempotencyKey,
        string reason,
        CancellationToken cancellationToken)
    {
        AllocationReversalKeyRecord? committed = await _reversalLookup.FindByKeyAsync(
            organizationId,
            reversalIdempotencyKey,
            cancellationToken);

        if (committed is not null)
        {
            return Classify(committed, allocationId, reason);
        }

        AllocationReversalState? state = await _reversalLookup.FindAllocationStateAsync(
            organizationId,
            allocationId,
            cancellationToken);

        if (state?.Status == AllocationStatus.Reversed)
        {
            return Result<ReverseAllocationResponse>.Failure(
                ReconciliationErrors.AllocationAlreadyReversed);
        }

        return Result<ReverseAllocationResponse>.Failure(
            ReconciliationApplicationErrors.ConcurrencyConflict);
    }

    private static Result<ReverseAllocationResponse> Classify(
        AllocationReversalKeyRecord existing,
        AllocationId allocationId,
        string reason)
    {
        if (!HasReversalMetadata(existing, out DateTimeOffset reversedAt, out string storedReason))
        {
            return Result<ReverseAllocationResponse>.Failure(
                ReconciliationApplicationErrors.AllocationReversalIntegrityConflict);
        }

        if (existing.AllocationId != allocationId
            || !string.Equals(storedReason, reason, StringComparison.Ordinal))
        {
            return Result<ReverseAllocationResponse>.Failure(
                ReconciliationApplicationErrors.AllocationReversalIdempotencyPayloadConflict);
        }

        if (existing.ReversalAuditCount == 1
            && existing.Audit is AllocationReversalAuditRecord audit
            && audit.AllocationId == existing.AllocationId
            && audit.InvoiceId == existing.InvoiceId
            && audit.BankTransactionId == existing.BankTransactionId
            && audit.Action == FinancialAuditAction.Reversal
            && audit.Operation == AllocationIdempotencyOperation.Reversal
            && audit.IdempotencyKey == existing.ReversalIdempotencyKey
            && audit.Amount == existing.Amount
            && audit.RecordedAt == reversedAt
            && string.Equals(audit.Reason, storedReason, StringComparison.Ordinal))
        {
            return Result<ReverseAllocationResponse>.Success(
                ToResponse(
                    existing.AllocationId,
                    existing.Amount,
                    reversedAt,
                    storedReason,
                    replayed: true));
        }

        return Result<ReverseAllocationResponse>.Failure(
            ReconciliationApplicationErrors.AllocationReversalIntegrityConflict);
    }

    private static bool HasReversalMetadata(
        AllocationReversalKeyRecord existing,
        out DateTimeOffset reversedAt,
        out string storedReason)
    {
        if (existing.Status == AllocationStatus.Reversed
            && existing.ReversedAt is DateTimeOffset storedReversedAt
            && existing.ReversalReason is string reason
            && reason.Length > 0
            && reason.Length <= Allocation.MaxReversalReasonLength
            && reason == reason.Trim())
        {
            reversedAt = storedReversedAt;
            storedReason = reason;
            return true;
        }

        reversedAt = default;
        storedReason = string.Empty;
        return false;
    }

    private static ReverseAllocationResponse ToResponse(
        AllocationId allocationId,
        Money amount,
        DateTimeOffset reversedAt,
        string reason,
        bool replayed)
    {
        return new ReverseAllocationResponse(
            allocationId.Value,
            "reversed",
            new MoneyDto(amount.Amount, amount.Currency.Value),
            reversedAt,
            reason,
            replayed);
    }
}