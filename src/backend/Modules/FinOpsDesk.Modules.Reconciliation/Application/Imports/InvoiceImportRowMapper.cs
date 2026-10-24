using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Imports;
using FinOpsDesk.SharedKernel.Results;

namespace FinOpsDesk.Modules.Reconciliation.Application.Imports;

public static class InvoiceImportRowMapper
{
    public static PreparedImportRow<InvoiceImportFacts> Map(ParsedCsvRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (!TryReadExternalId(row, out string? externalId, out PreparedImportRow<InvoiceImportFacts>? failure))
        {
            return failure!;
        }

        if (!row.Fields.TryGetValue("InvoiceNumber", out string? invoiceNumber)
            || string.IsNullOrWhiteSpace(invoiceNumber))
        {
            return Invalid(row, externalId, ImportRowErrors.InvalidInvoiceNumber, "InvoiceNumber");
        }

        if (!row.Fields.TryGetValue("Amount", out string? amountText)
            || !ImportedAmountParser.TryParse(amountText, out decimal amount))
        {
            return Invalid(row, externalId, ImportRowErrors.InvalidAmount, "Amount");
        }

        if (!TryReadCurrency(row, out CurrencyCode? currency))
        {
            return Invalid(row, externalId, ImportRowErrors.InvalidCurrency, "Currency");
        }

        return new PreparedImportRow<InvoiceImportFacts>(
            row.DataRowIndex,
            row.SourceLineNumber,
            externalId,
            new InvoiceImportFacts(
                externalId!,
                invoiceNumber.Trim(),
                new Money(amount, currency!)),
            Error: null,
            ColumnName: null);
    }

    internal static bool TryReadExternalId<TFacts>(
        ParsedCsvRow row,
        out string? externalId,
        out PreparedImportRow<TFacts>? failure)
        where TFacts : class
    {
        externalId = null;
        failure = null;

        if (!row.Fields.TryGetValue("ExternalId", out string? raw))
        {
            failure = Invalid<TFacts>(row, null, ImportRowErrors.InvalidExternalId, "ExternalId");
            return false;
        }

        try
        {
            externalId = new ExternalId(raw).Value;
            return true;
        }
        catch (ArgumentException)
        {
            failure = Invalid<TFacts>(row, null, ImportRowErrors.InvalidExternalId, "ExternalId");
            return false;
        }
    }

    internal static bool TryReadCurrency(ParsedCsvRow row, out CurrencyCode? currency)
    {
        currency = null;

        if (!row.Fields.TryGetValue("Currency", out string? raw))
        {
            return false;
        }

        try
        {
            currency = new CurrencyCode(raw);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    internal static PreparedImportRow<TFacts> Invalid<TFacts>(
        ParsedCsvRow row,
        string? externalId,
        Error error,
        string columnName)
        where TFacts : class
    {
        return new PreparedImportRow<TFacts>(
            row.DataRowIndex,
            row.SourceLineNumber,
            externalId,
            Facts: null,
            error,
            columnName);
    }

    private static PreparedImportRow<InvoiceImportFacts> Invalid(
        ParsedCsvRow row,
        string? externalId,
        Error error,
        string columnName)
    {
        return Invalid<InvoiceImportFacts>(row, externalId, error, columnName);
    }
}