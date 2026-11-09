namespace FinOpsDesk.Modules.Reconciliation.Application.Allocations;

internal static class AllocationDisputeListPagination
{
    public const int DefaultPage = 1;

    public const int DefaultPageSize = 50;

    public const int MinimumPage = 1;

    public const int MinimumPageSize = 1;

    public const int MaximumPageSize = 100;

    public static bool IsValid(int page, int pageSize)
    {
        if (page < MinimumPage
            || pageSize < MinimumPageSize
            || pageSize > MaximumPageSize)
        {
            return false;
        }

        long offset = ((long)page - 1L) * pageSize;
        return offset <= int.MaxValue;
    }

    public static bool IsValidPageSize(int pageSize)
    {
        return pageSize >= MinimumPageSize && pageSize <= MaximumPageSize;
    }
}