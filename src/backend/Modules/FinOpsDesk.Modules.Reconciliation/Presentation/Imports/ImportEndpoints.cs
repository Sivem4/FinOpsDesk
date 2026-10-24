using System.Buffers;

using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.Imports;
using FinOpsDesk.Modules.Reconciliation.Domain.Imports;
using FinOpsDesk.SharedKernel.Results;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace FinOpsDesk.Modules.Reconciliation.Presentation.Imports;

internal static class ImportEndpoints
{
    public static RouteGroupBuilder MapImportEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        LimitUploads(group.MapPost("/imports/invoices", ImportInvoicesAsync));
        LimitUploads(group.MapPost("/imports/bank-transactions", ImportBankTransactionsAsync));

        return group;
    }

    private static RouteHandlerBuilder LimitUploads(RouteHandlerBuilder builder)
    {
        return builder
            .WithMetadata(new RequestSizeLimitAttribute(ImportLimits.MaxMultipartRequestBytes))
            .WithMetadata(new RequestFormLimitsAttribute
            {
                MultipartBodyLengthLimit = ImportLimits.MaxMultipartRequestBytes,
                ValueLengthLimit = ImportLimits.MaxFormValueLength,
                MultipartHeadersLengthLimit = ImportLimits.MaxMultipartHeadersLength,
            })
            .DisableAntiforgery();
    }

    private static Task<Results<JsonHttpResult<ImportCsvResponse>, ProblemHttpResult>> ImportInvoicesAsync(
        IFormFile? file,
        [FromForm] string? sourceSystem,
        ImportCsvHandler handler,
        CancellationToken cancellationToken)
    {
        return ImportAsync(file, sourceSystem, ImportType.Invoice, handler, cancellationToken);
    }

    private static Task<Results<JsonHttpResult<ImportCsvResponse>, ProblemHttpResult>> ImportBankTransactionsAsync(
        IFormFile? file,
        [FromForm] string? sourceSystem,
        ImportCsvHandler handler,
        CancellationToken cancellationToken)
    {
        return ImportAsync(
            file,
            sourceSystem,
            ImportType.BankTransaction,
            handler,
            cancellationToken);
    }

    private static async Task<Results<JsonHttpResult<ImportCsvResponse>, ProblemHttpResult>> ImportAsync(
        IFormFile? file,
        string? sourceSystem,
        ImportType importType,
        ImportCsvHandler handler,
        CancellationToken cancellationToken)
    {
        if (file is null)
        {
            return Problem(ReconciliationApplicationErrors.ImportFileRequired, StatusCodes.Status400BadRequest);
        }

        if (file.Length > ImportLimits.MaxFileBytes)
        {
            return Problem(
                ReconciliationApplicationErrors.ImportFileTooLarge,
                StatusCodes.Status413PayloadTooLarge);
        }

        Result<byte[]> content = await ReadBoundedAsync(file, cancellationToken);

        if (content.IsFailure)
        {
            return Problem(content.Error, StatusCodes.Status413PayloadTooLarge);
        }

        Result<ImportCsvResponse> result = await handler.Handle(
            new ImportCsvCommand(importType, sourceSystem, content.Value, file.FileName),
            cancellationToken);

        if (result.IsFailure)
        {
            return Problem(result.Error, StatusCodeFor(result.Error));
        }

        int statusCode = result.Value.Replayed
            ? StatusCodes.Status200OK
            : StatusCodes.Status201Created;

        return TypedResults.Json(result.Value, statusCode: statusCode);
    }

    private static async Task<Result<byte[]>> ReadBoundedAsync(
        IFormFile file,
        CancellationToken cancellationToken)
    {
        await using Stream stream = file.OpenReadStream();
        using var buffer = new MemoryStream();
        byte[] rented = ArrayPool<byte>.Shared.Rent(16 * 1024);

        try
        {
            while (true)
            {
                int read = await stream.ReadAsync(rented.AsMemory(0, rented.Length), cancellationToken);

                if (read == 0)
                {
                    break;
                }

                if (buffer.Length + read > ImportLimits.MaxFileBytes)
                {
                    return Result<byte[]>.Failure(ReconciliationApplicationErrors.ImportFileTooLarge);
                }

                buffer.Write(rented, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        return Result<byte[]>.Success(buffer.ToArray());
    }

    private static int StatusCodeFor(Error error)
    {
        if (error.Code == ReconciliationApplicationErrors.ImportFileTooLarge.Code)
        {
            return StatusCodes.Status413PayloadTooLarge;
        }

        if (error.Code == ReconciliationApplicationErrors.ImportConflictUnresolved.Code)
        {
            return StatusCodes.Status409Conflict;
        }

        return StatusCodes.Status400BadRequest;
    }

    private static ProblemHttpResult Problem(Error error, int statusCode)
    {
        return TypedResults.Problem(
            detail: error.Message,
            statusCode: statusCode,
            title: TitleFor(error),
            extensions: new Dictionary<string, object?>
            {
                ["code"] = error.Code,
            });
    }

    private static string TitleFor(Error error)
    {
        if (error.Code == ReconciliationApplicationErrors.ImportFileRequired.Code)
        {
            return "Import file is required.";
        }

        if (error.Code == ReconciliationApplicationErrors.ImportInvalidSourceSystem.Code)
        {
            return "Invalid source system.";
        }

        if (error.Code == ReconciliationApplicationErrors.ImportTooManyRows.Code)
        {
            return "The CSV file has too many rows.";
        }

        if (error.Code == ReconciliationApplicationErrors.ImportFileTooLarge.Code)
        {
            return "File is too large.";
        }

        if (error.Code == ReconciliationApplicationErrors.ImportConflictUnresolved.Code)
        {
            return "Import conflict was not resolved.";
        }

        return "The CSV file is invalid.";
    }
}