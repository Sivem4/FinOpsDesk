using FinOpsDesk.Modules.Reconciliation.Application.Abstractions;
using FinOpsDesk.Modules.Reconciliation.Application.Invoices;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Application.Allocations;

public sealed class GetAllocationContextHandler
{
    private readonly ICurrentOrganizationContext _currentOrganizationContext;
    private readonly IAllocationContextQueries _queries;

    public GetAllocationContextHandler(
        ICurrentOrganizationContext currentOrganizationContext,
        IAllocationContextQueries queries)
    {
        ArgumentNullException.ThrowIfNull(currentOrganizationContext);
        ArgumentNullException.ThrowIfNull(queries);

        _currentOrganizationContext = currentOrganizationContext;
        _queries = queries;
    }

    public async Task<Result<AllocationContextResponse>> Handle(
        GetAllocationContextQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        OrganizationId organizationId = _currentOrganizationContext.OrganizationId;
        AllocationContextInvoiceColumns? invoice = await _queries.ReadInvoiceAsync(
            organizationId,
            query.InvoiceId,
            cancellationToken);

        if (invoice is null)
        {
            return Result<AllocationContextResponse>.Failure(
                ReconciliationApplicationErrors.InvoiceNotFound);
        }

        if (!TryMapInvoice(invoice, out AllocationContextInvoice mappedInvoice))
        {
            return Result<AllocationContextResponse>.Failure(
                ReconciliationApplicationErrors.AllocationContextInconsistentMoney);
        }

        AllocationContextBankTransactionColumns? bankTransaction = await _queries.ReadBankTransactionAsync(
            organizationId,
            query.BankTransactionId,
            cancellationToken);

        if (bankTransaction is null)
        {
            return Result<AllocationContextResponse>.Failure(
                ReconciliationApplicationErrors.BankTransactionNotFound);
        }

        if (!TryMapBankTransaction(bankTransaction, out AllocationContextBankTransaction mappedBankTransaction))
        {
            return Result<AllocationContextResponse>.Failure(
                ReconciliationApplicationErrors.AllocationContextInconsistentMoney);
        }

        return Result<AllocationContextResponse>.Success(
            new AllocationContextResponse(mappedInvoice, mappedBankTransaction));
    }

    private static bool TryMapInvoice(
        AllocationContextInvoiceColumns columns,
        out AllocationContextInvoice invoice)
    {
        invoice = null!;
        if (!AllocationContextAmounts.TryRead(
                columns.TotalAmount,
                columns.TotalCurrency,
                columns.AllocatedAmount,
                columns.AllocatedCurrency,
                out MoneyDto total,
                out MoneyDto allocated,
                out MoneyDto outstanding))
        {
            return false;
        }

        invoice = new AllocationContextInvoice(
            columns.InvoiceId,
            columns.InvoiceNumber,
            total,
            allocated,
            outstanding,
            columns.SourceSystem,
            columns.ExternalId);
        return true;
    }

    private static bool TryMapBankTransaction(
        AllocationContextBankTransactionColumns columns,
        out AllocationContextBankTransaction bankTransaction)
    {
        bankTransaction = null!;
        if (!AllocationContextAmounts.TryRead(
                columns.Amount,
                columns.Currency,
                columns.AllocatedAmount,
                columns.AllocatedCurrency,
                out MoneyDto amount,
                out MoneyDto allocated,
                out MoneyDto unapplied))
        {
            return false;
        }

        bankTransaction = new AllocationContextBankTransaction(
            columns.BankTransactionId,
            columns.Reference,
            columns.BookingDate,
            amount,
            allocated,
            unapplied,
            columns.SourceSystem,
            columns.ExternalId);
        return true;
    }
}