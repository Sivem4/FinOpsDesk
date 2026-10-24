using FinOpsDesk.Modules.Reconciliation.Domain.Imports;
using FinOpsDesk.SharedKernel.Results;

namespace FinOpsDesk.Modules.Reconciliation.Application.Imports;

public interface IImportFileReader
{
    Result<ParsedCsvFile> Read(
        ImportType importType,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken);
}