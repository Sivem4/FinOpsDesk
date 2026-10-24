using System.Globalization;
using System.Text;

using CsvHelper;
using CsvHelper.Configuration;

using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.Imports;
using FinOpsDesk.Modules.Reconciliation.Domain.Imports;
using FinOpsDesk.SharedKernel.Results;

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Imports;

internal sealed class CsvHelperImportFileReader : IImportFileReader
{
    private static readonly string[] InvoiceHeaders =
    [
        "ExternalId",
        "InvoiceNumber",
        "Amount",
        "Currency"
    ];

    private static readonly string[] BankTransactionHeaders =
    [
        "ExternalId",
        "BookingDate",
        "Amount",
        "Currency",
        "Reference"
    ];

    public Result<ParsedCsvFile> Read(
        ImportType importType,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(importType))
        {
            throw new ArgumentOutOfRangeException(nameof(importType));
        }

        cancellationToken.ThrowIfCancellationRequested();

        string text;

        try
        {
            text = DecodeUtf8(content.Span);
        }
        catch (DecoderFallbackException)
        {
            return Result<ParsedCsvFile>.Failure(
                ReconciliationApplicationErrors.ImportInvalidFile);
        }

        if (text.Length == 0)
        {
            return Result<ParsedCsvFile>.Failure(
                ReconciliationApplicationErrors.ImportInvalidFile);
        }

        string[] expectedHeaders = importType == ImportType.Invoice
            ? InvoiceHeaders
            : BankTransactionHeaders;

        try
        {
            return ReadRecords(text, expectedHeaders, cancellationToken);
        }
        catch (CsvHelperException)
        {
            return Result<ParsedCsvFile>.Failure(
                ReconciliationApplicationErrors.ImportInvalidFile);
        }
        catch (FormatException)
        {
            return Result<ParsedCsvFile>.Failure(
                ReconciliationApplicationErrors.ImportInvalidFile);
        }
    }

    private static Result<ParsedCsvFile> ReadRecords(
        string text,
        string[] expectedHeaders,
        CancellationToken cancellationToken)
    {
        var configuration = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            DetectDelimiter = false,
            Delimiter = ",",
            Mode = CsvMode.RFC4180,
            TrimOptions = TrimOptions.None,
            IgnoreBlankLines = false,
            DetectColumnCountChanges = true,
            ExceptionMessagesContainRawData = false,
            HeaderValidated = null,
            ShouldSkipRecord = _ => false,
            BadDataFound = _ => throw new FormatException("The CSV record is malformed."),
            MissingFieldFound = _ => throw new FormatException("The CSV record has the wrong number of fields."),
        };

        using var reader = new StringReader(text);
        using var csv = new CsvReader(reader, configuration);

        if (!csv.Read() || !csv.ReadHeader() || csv.HeaderRecord is null)
        {
            return Result<ParsedCsvFile>.Failure(
                ReconciliationApplicationErrors.ImportInvalidFile);
        }

        if (!HeaderMatches(csv.HeaderRecord, expectedHeaders))
        {
            return Result<ParsedCsvFile>.Failure(
                ReconciliationApplicationErrors.ImportInvalidFile);
        }

        var rows = new List<ParsedCsvRow>();

        while (csv.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (rows.Count == ImportLimits.MaxDataRows)
            {
                return Result<ParsedCsvFile>.Failure(
                    ReconciliationApplicationErrors.ImportTooManyRows);
            }

            int sourceLineNumber = FirstPhysicalLine(csv.Parser.RawRow, csv.Parser.RawRecord);

            if (sourceLineNumber < 1)
            {
                throw new InvalidOperationException(
                    "The CSV reader could not determine the first physical line of a record.");
            }

            var fields = new Dictionary<string, string>(expectedHeaders.Length, StringComparer.Ordinal);

            for (int index = 0; index < expectedHeaders.Length; index++)
            {
                fields[csv.HeaderRecord[index]] = csv.GetField(index) ?? string.Empty;
            }

            rows.Add(new ParsedCsvRow(rows.Count + 1, sourceLineNumber, fields));
        }

        if (rows.Count == 0)
        {
            return Result<ParsedCsvFile>.Failure(
                ReconciliationApplicationErrors.ImportInvalidFile);
        }

        return Result<ParsedCsvFile>.Success(new ParsedCsvFile(rows));
    }

    internal static int FirstPhysicalLine(int finalPhysicalLine, string? rawRecord)
    {
        // CsvHelper 33.1.0 sets Parser.RawRow to the record's final physical line.
        // RawRecord includes that record's terminating line break when one was read.
        int internalBreaks = CountLineBreaks(rawRecord) - (EndsWithLineBreak(rawRecord) ? 1 : 0);

        return finalPhysicalLine - internalBreaks;
    }

    private static bool HeaderMatches(string[] actual, string[] expected)
    {
        if (actual.Length != expected.Length)
        {
            return false;
        }

        var seen = new HashSet<string>(actual.Length, StringComparer.Ordinal);

        foreach (string header in actual)
        {
            if (!seen.Add(header))
            {
                return false;
            }
        }

        foreach (string header in expected)
        {
            if (!seen.Contains(header))
            {
                return false;
            }
        }

        return true;
    }

    private static string DecodeUtf8(ReadOnlySpan<byte> content)
    {
        int offset = content.Length >= 3
            && content[0] == 0xEF
            && content[1] == 0xBB
            && content[2] == 0xBF
            ? 3
            : 0;
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        return encoding.GetString(content[offset..]);
    }

    private static int CountLineBreaks(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return 0;
        }

        int count = 0;

        for (int index = 0; index < value.Length; index++)
        {
            if (value[index] == '\r')
            {
                count++;

                if (index + 1 < value.Length && value[index + 1] == '\n')
                {
                    index++;
                }
            }
            else if (value[index] == '\n')
            {
                count++;
            }
        }

        return count;
    }

    private static bool EndsWithLineBreak(string? value)
    {
        return !string.IsNullOrEmpty(value)
            && (value[^1] == '\n' || value[^1] == '\r');
    }
}