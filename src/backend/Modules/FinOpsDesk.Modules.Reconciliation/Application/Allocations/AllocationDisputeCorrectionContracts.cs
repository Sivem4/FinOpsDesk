using FinOpsDesk.Modules.Reconciliation.Application.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.FinancialAudit;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Application.Allocations;

public sealed record CorrectAllocationDisputeCommand(
    AllocationDisputeId DisputeId,
    string Reason,
    Guid IdempotencyKey);

public sealed record CorrectAllocationDisputeResponse(
    Guid DisputeId,
    Guid AllocationId,
    Guid InvoiceId,
    Guid BankTransactionId,
    string Status,
    MoneyDto Amount,
    DateTimeOffset ReversedAt,
    string Reason,
    string ActorSource,
    string ActorId,
    bool Replayed);

public sealed record GetAllocationDisputeCorrectionQuery(Guid DisputeId);

public sealed record AllocationDisputeCorrectionStateResponse(
    Guid DisputeId,
    AllocationDisputeCorrectionView? Correction);

public sealed record AllocationDisputeCorrectionView(
    Guid DisputeId,
    Guid AllocationId,
    Guid InvoiceId,
    Guid BankTransactionId,
    DateTimeOffset ReversedAt,
    string Reason,
    string ActorSource,
    string ActorId,
    MoneyDto Amount);

public sealed record AllocationDisputeTarget(AllocationId AllocationId);

public sealed record AllocationDisputeCorrectionLink(
    AllocationDisputeId DisputeId,
    AllocationId AllocationId,
    Guid IdempotencyKey,
    string Reason,
    string ActorId,
    ReviewCaseActorSource ActorSource,
    DateTimeOffset CorrectedAt);

public sealed record AllocationReversalFacts(
    AllocationId AllocationId,
    InvoiceId InvoiceId,
    BankTransactionId BankTransactionId,
    AllocationStatus Status,
    Money Amount,
    string? ReversalReason,
    DateTimeOffset? ReversedAt,
    Guid? ReversalIdempotencyKey);

public sealed record AllocationReversalAuditFact(
    AllocationId AllocationId,
    InvoiceId InvoiceId,
    BankTransactionId BankTransactionId,
    FinancialAuditAction Action,
    AllocationIdempotencyOperation Operation,
    Guid IdempotencyKey,
    Money Amount,
    DateTimeOffset RecordedAt,
    string? Reason,
    string ActorId);

/// <summary>
/// Test seam between classification reads. Production calls complete immediately.
/// The signal does not commit, roll back, retry a write, or classify a result.
/// </summary>
public interface IAllocationDisputeCorrectionReadSignals
{
    Task OnReadAsync(string step, CancellationToken cancellationToken);
}

public sealed class NoAllocationDisputeCorrectionReadSignals : IAllocationDisputeCorrectionReadSignals
{
    public Task OnReadAsync(string step, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(step);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}

public interface IAllocationDisputeCorrectionReads
{
    Task<AllocationDisputeCorrectionLink?> FindCorrectionByKeyAsync(
        OrganizationId organizationId,
        Guid idempotencyKey,
        CancellationToken cancellationToken);

    Task<AllocationReversalFacts?> FindReversalByKeyAsync(
        OrganizationId organizationId,
        Guid idempotencyKey,
        CancellationToken cancellationToken);

    Task<AllocationDisputeCorrectionLink?> FindCorrectionByDisputeAsync(
        OrganizationId organizationId,
        AllocationDisputeId disputeId,
        CancellationToken cancellationToken);

    Task<AllocationReversalFacts?> FindAllocationAsync(
        OrganizationId organizationId,
        AllocationId allocationId,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<AllocationReversalAuditFact>> ListReversalAuditsAsync(
        OrganizationId organizationId,
        AllocationId allocationId,
        CancellationToken cancellationToken);

    Task<AllocationDisputeTarget?> FindDisputeAsync(
        OrganizationId organizationId,
        AllocationDisputeId disputeId,
        CancellationToken cancellationToken);

    Task<AllocationDisputeResolutionOutcome?> FindResolutionOutcomeAsync(
        OrganizationId organizationId,
        AllocationDisputeId disputeId,
        CancellationToken cancellationToken);
}

public interface IAllocationDisputeCorrectionWork
{
    Task<IAllocationDisputeCorrectionSession> BeginAsync(
        OrganizationId organizationId,
        AllocationId allocationId,
        CancellationToken cancellationToken);
}

public interface IAllocationDisputeCorrectionSession : IAllocationDisputeCorrectionReads, IAsyncDisposable
{
    bool AllocationFound { get; }

    void Add(AllocationDisputeCorrection correction);

    Task CommitAsync(CancellationToken cancellationToken);

    Task RollbackAsync(CancellationToken cancellationToken);
}

public interface IAllocationDisputeCorrectionConflict
{
    bool IsRecognizedUniqueConflict(Exception exception);

    bool IsDisputeOrAllocationForeignKey(Exception exception);
}