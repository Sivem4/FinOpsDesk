namespace FinOpsDesk.Modules.Reconciliation.Application.Allocations;

internal static class AllocationHistoryListPagination
{
    public const int DefaultPage = 1;

    public const int DefaultPageSize = 50;

    public const int MinPage = 1;

    public const int MinPageSize = 1;

    public const int MaxPageSize = 100;

    public static bool IsValid(int page, int pageSize)
    {
        if (page < MinPage || pageSize < MinPageSize || pageSize > MaxPageSize)
        {
            return false;
        }

        long offset = ((long)page - 1L) * pageSize;
        return offset <= int.MaxValue;
    }
}