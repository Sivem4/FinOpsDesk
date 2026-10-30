namespace FinOpsDesk.Modules.Reconciliation.Application.Invoices;

public sealed class ListInvoicesQuery
{
    public int Page { get; }

    public int PageSize { get; }

    public string? InvoiceNumber { get; }

    public ListInvoicesQuery(int page, int pageSize, string? invoiceNumber = null)
    {
        Page = page;
        PageSize = pageSize;
        InvoiceNumber = invoiceNumber;
    }
}