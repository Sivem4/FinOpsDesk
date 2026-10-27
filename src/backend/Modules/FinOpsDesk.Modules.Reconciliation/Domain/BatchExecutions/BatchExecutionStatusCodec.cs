namespace FinOpsDesk.Modules.Reconciliation.Domain.BatchExecutions;

public static class BatchExecutionStatusCodec
{
    public static int ToInt(BatchExecutionStatus status)
    {
        if (!Enum.IsDefined(status))
        {
            throw new ArgumentOutOfRangeException(
                nameof(status),
                "Batch execution status is not defined.");
        }

        return (int)status;
    }

    public static BatchExecutionStatus ToStatus(int value)
    {
        if (!Enum.IsDefined(typeof(BatchExecutionStatus), value))
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                "Batch execution status is not defined.");
        }

        return (BatchExecutionStatus)value;
    }
}