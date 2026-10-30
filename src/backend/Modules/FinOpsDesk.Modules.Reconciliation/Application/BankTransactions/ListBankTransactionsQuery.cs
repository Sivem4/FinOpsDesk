namespace FinOpsDesk.Modules.Reconciliation.Application.BankTransactions;

public sealed class ListBankTransactionsQuery
{
    public int Page { get; }

    public int PageSize { get; }

    public ListBankTransactionsQuery(int page, int pageSize)
    {
        Page = page;
        PageSize = pageSize;
    }
}