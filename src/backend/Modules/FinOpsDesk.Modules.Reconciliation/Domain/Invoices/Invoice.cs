using FinOpsDesk.Modules.Reconciliation.Domain;

namespace FinOpsDesk.Modules.Reconciliation.Domain.Invoices;

public sealed class Invoice
{
    public InvoiceId Id { get; }

    public string InvoiceNumber { get; }

    public Money TotalAmount { get; }

    public Money AllocatedAmount { get; private set; }

    public Money OutstandingAmount =>
        TotalAmount.Subtract(AllocatedAmount);

    public Invoice(
        InvoiceId id,
        string invoiceNumber,
        Money totalAmount)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(invoiceNumber);
        ArgumentNullException.ThrowIfNull(totalAmount);

        if (totalAmount.Amount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(totalAmount),
                "Invoice total amount must be greater than zero.");
        }

        Id = id;
        InvoiceNumber = invoiceNumber.Trim();
        TotalAmount = totalAmount;

        AllocatedAmount = Money.Zero(totalAmount.Currency);
    }

    public void Allocate(Money amount)
    {
        ArgumentNullException.ThrowIfNull(amount);

        if (amount.Amount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(amount),
                "Allocation amount must be greater than zero.");
        }

        EnsureSameCurrency(amount);

        if (amount.Amount > OutstandingAmount.Amount)
        {
            throw new InvalidOperationException(
                "Allocation amount cannot exceed invoice outstanding amount.");
        }

        AllocatedAmount = AllocatedAmount.Add(amount);
    }

    public void ReverseAllocation(Money amount)
    {
        ArgumentNullException.ThrowIfNull(amount);

        if (amount.Amount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(amount),
                "Reversal amount must be greater than zero.");
        }

        EnsureSameCurrency(amount);

        if (amount.Amount > AllocatedAmount.Amount)
        {
            throw new InvalidOperationException(
                "Reversal amount cannot exceed invoice allocated amount.");
        }

        AllocatedAmount = AllocatedAmount.Subtract(amount);
    }

    private void EnsureSameCurrency(Money amount)
    {
        if (TotalAmount.Currency != amount.Currency)
        {
            throw new InvalidOperationException(
                $"Invoice currency is {TotalAmount.Currency}, but operation currency is {amount.Currency}.");
        }
    }
}