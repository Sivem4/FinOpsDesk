using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;

namespace FinOpsDesk.Modules.Reconciliation.Domain.MatchSuggestions;

public sealed class MatchSuggestion
{
    public MatchSuggestionId Id { get; }

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
        InvoiceId invoiceId,
        BankTransactionId bankTransactionId,
        Money suggestedAmount,
        DateTimeOffset generatedAt)
    {
        ArgumentNullException.ThrowIfNull(id);
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
        InvoiceId = invoiceId;
        BankTransactionId = bankTransactionId;
        SuggestedAmount = suggestedAmount;
        GeneratedAt = generatedAt;
        Status = MatchSuggestionStatus.Pending;
    }

    public void Accept(
        AllocationId allocationId,
        DateTimeOffset resolvedAt)
    {
        if (Status != MatchSuggestionStatus.Pending)
        {
            throw new InvalidOperationException(
                "A match suggestion can only be accepted or rejected while pending.");
        }

        ArgumentNullException.ThrowIfNull(allocationId);

        if (resolvedAt < GeneratedAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(resolvedAt),
                "Resolution time cannot precede generation time.");
        }

        Status = MatchSuggestionStatus.Accepted;
        ResolvedAt = resolvedAt;
        AllocationId = allocationId;
    }

    public void Reject(
        string reason,
        DateTimeOffset resolvedAt)
    {
        if (Status != MatchSuggestionStatus.Pending)
        {
            throw new InvalidOperationException(
                "A match suggestion can only be accepted or rejected while pending.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (resolvedAt < GeneratedAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(resolvedAt),
                "Resolution time cannot precede generation time.");
        }

        Status = MatchSuggestionStatus.Rejected;
        ResolvedAt = resolvedAt;
        RejectionReason = reason.Trim();
    }
}