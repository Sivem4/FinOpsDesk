using System.Globalization;

using FinOpsDesk.Modules.Reconciliation.Domain;

namespace FinOpsDesk.Modules.Reconciliation.Application.Imports;

public static class BankTransactionImportRowMapper
{
    public static PreparedImportRow<BankTransactionImportFacts> Map(ParsedCsvRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (!InvoiceImportRowMapper.TryReadExternalId(
                row,
                out string? externalId,
                out PreparedImportRow<BankTransactionImportFacts>? failure))
        {
            return failure!;
        }

        if (!TryReadBookingDate(row, out DateOnly bookingDate))
        {
            return InvoiceImportRowMapper.Invalid<BankTransactionImportFacts>(
                row,
                externalId,
                ImportRowErrors.InvalidBookingDate,
                "BookingDate");
        }

        if (!row.Fields.TryGetValue("Amount", out string? amountText)
            || !ImportedAmountParser.TryParse(amountText, out decimal amount))
        {
            return InvoiceImportRowMapper.Invalid<BankTransactionImportFacts>(
                row,
                externalId,
                ImportRowErrors.InvalidAmount,
                "Amount");
        }

        if (!InvoiceImportRowMapper.TryReadCurrency(row, out CurrencyCode? currency))
        {
            return InvoiceImportRowMapper.Invalid<BankTransactionImportFacts>(
                row,
                externalId,
                ImportRowErrors.InvalidCurrency,
                "Currency");
        }

        string? reference = null;

        if (row.Fields.TryGetValue("Reference", out string? rawReference)
            && !string.IsNullOrWhiteSpace(rawReference))
        {
            reference = rawReference.Trim();
        }

        return new PreparedImportRow<BankTransactionImportFacts>(
            row.DataRowIndex,
            row.SourceLineNumber,
            externalId,
            new BankTransactionImportFacts(
                externalId!,
                new Money(amount, currency!),
                bookingDate,
                reference),
            Error: null,
            ColumnName: null);
    }

    private static bool TryReadBookingDate(ParsedCsvRow row, out DateOnly bookingDate)
    {
        bookingDate = default;

        if (!row.Fields.TryGetValue("BookingDate", out string? raw)
            || string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        return DateOnly.TryParseExact(
            raw.Trim(),
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out bookingDate);
    }
}