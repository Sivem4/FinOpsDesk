using FinOpsDesk.Modules.Reconciliation.Application.Imports;

namespace FinOpsDesk.UnitTests.Reconciliation.Application.Imports;

public sealed class ImportRowValidationTests
{
    [Fact]
    public void First_invalid_invoice_field_is_the_only_row_error()
    {
        PreparedImportRow<InvoiceImportFacts> row = InvoiceImportRowMapper.Map(Row(
            ("ExternalId", "INV-1"),
            ("InvoiceNumber", " "),
            ("Amount", "0"),
            ("Currency", "PLN")));

        Assert.Null(row.Facts);
        Assert.Equal("INV-1", row.ExternalId);
        Assert.Equal(ImportRowErrors.InvalidInvoiceNumber.Code, row.Error!.Code);
        Assert.Equal("InvoiceNumber", row.ColumnName);
    }

    [Fact]
    public void Invalid_amount_does_not_clear_a_valid_external_id()
    {
        PreparedImportRow<InvoiceImportFacts> row = InvoiceImportRowMapper.Map(Row(
            ("ExternalId", "INV-1"),
            ("InvoiceNumber", "FV/1"),
            ("Amount", "1,000.00"),
            ("Currency", "PLN")));

        Assert.Equal("INV-1", row.ExternalId);
        Assert.Null(row.Facts);
        Assert.Equal(ImportRowErrors.InvalidAmount.Code, row.Error!.Code);
        Assert.Equal("Amount", row.ColumnName);
    }

    [Fact]
    public void Missing_external_id_does_not_reserve_an_identity()
    {
        PreparedImportRow<InvoiceImportFacts> row = InvoiceImportRowMapper.Map(Row(
            ("ExternalId", " "),
            ("InvoiceNumber", "FV/1"),
            ("Amount", "10.00"),
            ("Currency", "PLN")));

        Assert.Null(row.ExternalId);
        Assert.Null(row.Facts);
        Assert.Equal(ImportRowErrors.InvalidExternalId.Code, row.Error!.Code);
    }

    [Fact]
    public void Maps_a_valid_bank_transaction_and_blank_reference()
    {
        PreparedImportRow<BankTransactionImportFacts> row = BankTransactionImportRowMapper.Map(Row(
            ("ExternalId", "TXN-1"),
            ("BookingDate", "2026-08-24"),
            ("Amount", "10.00"),
            ("Currency", "pln"),
            ("Reference", " ")));

        Assert.Null(row.Error);
        Assert.Equal(new DateOnly(2026, 8, 24), row.Facts!.BookingDate);
        Assert.Null(row.Facts.Reference);
        Assert.Equal("PLN", row.Facts.Amount.Currency.Value);
    }

    private static ParsedCsvRow Row(params (string Name, string Value)[] fields)
    {
        return new ParsedCsvRow(
            1,
            2,
            fields.ToDictionary(field => field.Name, field => field.Value, StringComparer.Ordinal));
    }
}