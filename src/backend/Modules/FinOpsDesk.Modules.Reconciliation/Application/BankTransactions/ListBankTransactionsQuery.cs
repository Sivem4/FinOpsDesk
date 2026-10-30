namespace FinOpsDesk.Modules.Reconciliation.Application.BankTransactions;

public sealed class ListBankTransactionsQuery
{
    public int Page { get; }

    public int PageSize { get; }

    public string? Reference { get; }

    public ListBankTransactionsQuery(int page, int pageSize, string? reference = null)
    {
        Page = page;
        PageSize = pageSize;
        Reference = reference;
    }
}