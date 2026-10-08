namespace FinOpsDesk.Modules.Reconciliation.Application.Invoices;

public sealed class ListInvoicesQuery
{
    public int Page { get; }

    public int PageSize { get; }

    public ListInvoicesQuery(int page, int pageSize)
    {
        Page = page;
        PageSize = pageSize;
    }
}