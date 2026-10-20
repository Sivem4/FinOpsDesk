using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Imports;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Domain.Invoices;

public sealed class Invoice
{
    public InvoiceId Id { get; }

    public OrganizationId OrganizationId { get; }

    public SourceIdentity? SourceIdentity { get; }

    public string InvoiceNumber { get; }

    public Money TotalAmount { get; }

    public Money AllocatedAmount { get; private set; }

    public Money OutstandingAmount =>
        TotalAmount.Subtract(AllocatedAmount);

#pragma warning disable CS8618
    // Required for persistence materialization.
    private Invoice()
    {
    }
#pragma warning restore CS8618

    public Invoice(
        InvoiceId id,
        OrganizationId organizationId,
        string invoiceNumber,
        Money totalAmount)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(invoiceNumber);
        ArgumentNullException.ThrowIfNull(totalAmount);

        if (totalAmount.Amount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(totalAmount),
                "Invoice total amount must be greater than zero.");
        }

        Id = id;
        OrganizationId = organizationId;
        SourceIdentity = null;
        InvoiceNumber = invoiceNumber.Trim();
        TotalAmount = totalAmount;

        AllocatedAmount = Money.Zero(totalAmount.Currency);
    }

    public Invoice(
        InvoiceId id,
        OrganizationId organizationId,
        string invoiceNumber,
        Money totalAmount,
        SourceIdentity sourceIdentity)
        : this(
            id,
            organizationId,
            invoiceNumber,
            totalAmount)
    {
        ArgumentNullException.ThrowIfNull(sourceIdentity);

        SourceIdentity = sourceIdentity;
    }

    public bool HasEquivalentImportedBusinessData(
        string invoiceNumber,
        Money totalAmount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invoiceNumber);
        ArgumentNullException.ThrowIfNull(totalAmount);

        return string.Equals(
                InvoiceNumber,
                invoiceNumber.Trim(),
                StringComparison.Ordinal)
            && TotalAmount == totalAmount;
    }

    public Result Allocate(Money amount)
    {
        ArgumentNullException.ThrowIfNull(amount);

        if (amount.Amount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(amount),
                "Allocation amount must be greater than zero.");
        }

        if (!amount.HasSameCurrency(TotalAmount))
        {
            return Result.Failure(ReconciliationErrors.CurrencyMismatch);
        }

        if (amount.IsGreaterThan(OutstandingAmount))
        {
            return Result.Failure(
                ReconciliationErrors.InvoiceAllocationExceedsOutstanding);
        }

        AllocatedAmount = AllocatedAmount.Add(amount);
        return Result.Success();
    }

    public Result ReverseAllocation(Money amount)
    {
        ArgumentNullException.ThrowIfNull(amount);

        if (amount.Amount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(amount),
                "Reversal amount must be greater than zero.");
        }

        if (!amount.HasSameCurrency(TotalAmount))
        {
            return Result.Failure(ReconciliationErrors.CurrencyMismatch);
        }

        if (amount.IsGreaterThan(AllocatedAmount))
        {
            return Result.Failure(
                ReconciliationErrors.InvoiceReversalExceedsAllocated);
        }

        AllocatedAmount = AllocatedAmount.Subtract(amount);
        return Result.Success();
    }
}