using FinOpsDesk.Modules.Reconciliation.Domain.Imports;

namespace FinOpsDesk.Modules.Reconciliation.Application.Imports;

public static class ImportFileNames
{
    public const string FallbackFileName = "import.csv";

    public static string Sanitize(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return FallbackFileName;
        }

        string baseName = Path.GetFileName(fileName.Trim());

        if (string.IsNullOrWhiteSpace(baseName)
            || baseName.Length > ImportBatch.MaxFileNameLength
            || baseName.Contains('\\', StringComparison.Ordinal)
            || baseName.Contains('/', StringComparison.Ordinal))
        {
            return FallbackFileName;
        }

        return baseName;
    }
}