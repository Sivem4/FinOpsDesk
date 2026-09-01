using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Domain.Allocations;

public sealed class Allocation
{
    public AllocationId Id { get; }

    public OrganizationId OrganizationId { get; }

    public InvoiceId InvoiceId { get; }

    public BankTransactionId BankTransactionId { get; }

    public Money Amount { get; }

    public AllocationStatus Status { get; private set; }

    public DateTimeOffset ConfirmedAt { get; }

    public DateTimeOffset? ReversedAt { get; private set; }

    public string? ReversalReason { get; private set; }

    public Allocation(
        AllocationId id,
        OrganizationId organizationId,
        InvoiceId invoiceId,
        BankTransactionId bankTransactionId,
        Money amount,
        DateTimeOffset confirmedAt)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(invoiceId);
        ArgumentNullException.ThrowIfNull(bankTransactionId);
        ArgumentNullException.ThrowIfNull(amount);

        if (amount.Amount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(amount),
                "Allocation amount must be greater than zero.");
        }

        if (confirmedAt == default)
        {
            throw new ArgumentOutOfRangeException(
                nameof(confirmedAt),
                "Confirmation time cannot be the default value.");
        }

        Id = id;
        OrganizationId = organizationId;
        InvoiceId = invoiceId;
        BankTransactionId = bankTransactionId;
        Amount = amount;
        ConfirmedAt = confirmedAt;
        Status = AllocationStatus.Confirmed;
    }

    public void Reverse(
        string reason,
        DateTimeOffset reversedAt)
    {
        if (Status == AllocationStatus.Reversed)
        {
            throw new InvalidOperationException(
                "A reversed allocation cannot be reversed again.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (reversedAt < ConfirmedAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(reversedAt),
                "Reversal time cannot precede confirmation time.");
        }

        Status = AllocationStatus.Reversed;
        ReversedAt = reversedAt;
        ReversalReason = reason.Trim();
    }
}