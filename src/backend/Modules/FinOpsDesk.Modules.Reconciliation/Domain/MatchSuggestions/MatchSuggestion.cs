using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Domain.MatchSuggestions;

public sealed class MatchSuggestion
{
    public MatchSuggestionId Id { get; }

    public OrganizationId OrganizationId { get; }

    public InvoiceId InvoiceId { get; }

    public BankTransactionId BankTransactionId { get; }

    public Money SuggestedAmount { get; }

    public DateTimeOffset GeneratedAt { get; }

    public MatchSuggestionStatus Status { get; private set; }

    public DateTimeOffset? ResolvedAt { get; private set; }

    public AllocationId? AllocationId { get; private set; }

    public string? RejectionReason { get; private set; }

    public MatchSuggestion(
        MatchSuggestionId id,
        OrganizationId organizationId,
        InvoiceId invoiceId,
        BankTransactionId bankTransactionId,
        Money suggestedAmount,
        DateTimeOffset generatedAt)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(invoiceId);
        ArgumentNullException.ThrowIfNull(bankTransactionId);
        ArgumentNullException.ThrowIfNull(suggestedAmount);

        if (suggestedAmount.Amount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(suggestedAmount),
                "Suggested amount must be greater than zero.");
        }

        if (generatedAt == default)
        {
            throw new ArgumentOutOfRangeException(
                nameof(generatedAt),
                "Generation time cannot be the default value.");
        }

        Id = id;
        OrganizationId = organizationId;
        InvoiceId = invoiceId;
        BankTransactionId = bankTransactionId;
        SuggestedAmount = suggestedAmount;
        GeneratedAt = generatedAt;
        Status = MatchSuggestionStatus.Pending;
    }

    public Result Accept(
        AllocationId allocationId,
        DateTimeOffset resolvedAt)
    {
        ArgumentNullException.ThrowIfNull(allocationId);

        if (resolvedAt < GeneratedAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(resolvedAt),
                "Resolution time cannot precede generation time.");
        }

        if (Status != MatchSuggestionStatus.Pending)
        {
            return Result.Failure(
                ReconciliationErrors.MatchSuggestionAlreadyResolved);
        }

        Status = MatchSuggestionStatus.Accepted;
        ResolvedAt = resolvedAt;
        AllocationId = allocationId;
        return Result.Success();
    }

    public Result Reject(
        string reason,
        DateTimeOffset resolvedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (resolvedAt < GeneratedAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(resolvedAt),
                "Resolution time cannot precede generation time.");
        }

        if (Status != MatchSuggestionStatus.Pending)
        {
            return Result.Failure(
                ReconciliationErrors.MatchSuggestionAlreadyResolved);
        }

        Status = MatchSuggestionStatus.Rejected;
        ResolvedAt = resolvedAt;
        RejectionReason = reason.Trim();
        return Result.Success();
    }
}