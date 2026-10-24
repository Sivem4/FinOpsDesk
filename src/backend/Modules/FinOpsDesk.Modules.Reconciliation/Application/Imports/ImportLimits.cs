namespace FinOpsDesk.Modules.Reconciliation.Application.Imports;

public static class ImportLimits
{
    public const int MaxFileBytes = 2 * 1024 * 1024;

    public const int MaxMultipartOverheadBytes = 64 * 1024;

    public const int MaxMultipartRequestBytes = MaxFileBytes + MaxMultipartOverheadBytes;

    public const int MaxDataRows = 2000;

    public const int MaxFormValueLength = 8 * 1024;

    public const int MaxMultipartHeadersLength = 16 * 1024;

    public const int LookupChunkSize = 500;
}