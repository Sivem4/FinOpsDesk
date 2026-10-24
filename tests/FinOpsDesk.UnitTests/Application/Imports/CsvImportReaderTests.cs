using System.Text;

using FinOpsDesk.Modules.Reconciliation.Application;
using FinOpsDesk.Modules.Reconciliation.Application.Imports;
using FinOpsDesk.Modules.Reconciliation.Domain.Imports;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Imports;

namespace FinOpsDesk.UnitTests.Reconciliation.Application.Imports;

public sealed class CsvImportReaderTests
{
    private readonly CsvHelperImportFileReader _reader = new();

    [Fact]
    public void Reads_a_valid_invoice_file()
    {
        ParsedCsvFile file = Read(
            ImportType.Invoice,
            """
            ExternalId,InvoiceNumber,Amount,Currency
            INV-1,FV/1,10.00,PLN
            """);

        ParsedCsvRow row = Assert.Single(file.Rows);

        Assert.Equal(1, row.DataRowIndex);
        Assert.Equal(2, row.SourceLineNumber);
        Assert.Equal("INV-1", row.Fields["ExternalId"]);
        Assert.Equal("FV/1", row.Fields["InvoiceNumber"]);
    }

    [Fact]
    public void Reads_a_valid_bank_transaction_file()
    {
        ParsedCsvFile file = Read(
            ImportType.BankTransaction,
            """
            ExternalId,BookingDate,Amount,Currency,Reference
            TXN-1,2026-08-24,10.00,PLN,INV-1
            """);

        ParsedCsvRow row = Assert.Single(file.Rows);

        Assert.Equal("2026-08-24", row.Fields["BookingDate"]);
        Assert.Equal("INV-1", row.Fields["Reference"]);
    }

    [Fact]
    public void Accepts_headers_in_any_order_and_quoted_commas()
    {
        ParsedCsvFile file = Read(
            ImportType.Invoice,
            """
            Currency,Amount,InvoiceNumber,ExternalId
            PLN,10.00,"FV/1, branch",INV-1
            """);

        Assert.Equal("FV/1, branch", Assert.Single(file.Rows).Fields["InvoiceNumber"]);
    }

    [Fact]
    public void Records_the_first_physical_line_of_multiline_records()
    {
        string csv =
            "ExternalId,InvoiceNumber,Amount,Currency\n"
            + "\"INV\n1\",FV/1,10.00,PLN\n"
            + "\"INV\n\n2\",FV/2,20.00,PLN\n";

        ParsedCsvFile file = Read(ImportType.Invoice, csv);

        Assert.Equal(2, file.Rows[0].SourceLineNumber);
        Assert.Equal(1, file.Rows[0].DataRowIndex);
        Assert.Equal("INV\n1", file.Rows[0].Fields["ExternalId"]);
        Assert.Equal(4, file.Rows[1].SourceLineNumber);
        Assert.Equal(2, file.Rows[1].DataRowIndex);
    }

    [Fact]
    public void Records_multiline_lines_for_crlf_and_lf()
    {
        string lf =
            "ExternalId,InvoiceNumber,Amount,Currency\n"
            + "\"A\nB\",FV,1.00,PLN\n"
            + "C,FV2,2.00,PLN";
        string crlf =
            "ExternalId,InvoiceNumber,Amount,Currency\r\n"
            + "\"A\r\nB\",FV,1.00,PLN\r\n"
            + "C,FV2,2.00,PLN";

        ParsedCsvFile lfFile = Read(ImportType.Invoice, lf);
        ParsedCsvFile crlfFile = Read(ImportType.Invoice, crlf);

        Assert.Equal([2, 4], lfFile.Rows.Select(row => row.SourceLineNumber).ToArray());
        Assert.Equal([2, 4], crlfFile.Rows.Select(row => row.SourceLineNumber).ToArray());
        Assert.Equal([1, 2], lfFile.Rows.Select(row => row.DataRowIndex).ToArray());
    }

    [Theory]
    [InlineData("externalid,InvoiceNumber,Amount,Currency\nA,B,1,PLN\n")]
    [InlineData("ExternalId,InvoiceNumber,Amount\nA,B,1\n")]
    [InlineData("ExternalId,ExternalId,Amount,Currency\nA,B,1,PLN\n")]
    [InlineData("ExternalId,InvoiceNumber,Amount,Currency,Extra\nA,B,1,PLN,X\n")]
    [InlineData("ExternalId,InvoiceNumber,Amount,Currency\n")]
    [InlineData("")]
    [InlineData("ExternalId,InvoiceNumber,Amount,Currency\n\n")]
    [InlineData("ExternalId,InvoiceNumber,Amount,Currency\nA,B\n")]
    [InlineData("ExternalId,InvoiceNumber,Amount,Currency\n\"ABC,1,PLN\n")]
    public void Rejects_structurally_invalid_files(string csv)
    {
        Assert.Equal(
            ReconciliationApplicationErrors.ImportInvalidFile.Code,
            _reader.Read(ImportType.Invoice, Encoding.UTF8.GetBytes(csv), CancellationToken.None).Error!.Code);
    }

    [Fact]
    public void Does_not_skip_a_structurally_valid_empty_record()
    {
        ParsedCsvFile file = Read(
            ImportType.Invoice,
            "ExternalId,InvoiceNumber,Amount,Currency\n,,,\n");

        ParsedCsvRow row = Assert.Single(file.Rows);

        Assert.Equal(string.Empty, row.Fields["ExternalId"]);
        Assert.Equal(1, row.DataRowIndex);
        Assert.Equal(2, row.SourceLineNumber);
    }

    [Fact]
    public void Rejects_invalid_utf8()
    {
        byte[] bytes = [0x61, 0xFF, 0x62];

        Assert.Equal(
            ReconciliationApplicationErrors.ImportInvalidFile.Code,
            _reader.Read(ImportType.Invoice, bytes, CancellationToken.None).Error!.Code);
    }

    [Fact]
    public void Accepts_a_leading_bom()
    {
        byte[] body = Encoding.UTF8.GetBytes(
            "ExternalId,InvoiceNumber,Amount,Currency\nINV-1,FV,1.00,PLN\n");
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. body];

        ParsedCsvFile file = ReadBytes(ImportType.Invoice, bytes);

        Assert.Equal("INV-1", Assert.Single(file.Rows).Fields["ExternalId"]);
    }

    [Fact]
    public void Rejects_more_than_2000_data_rows()
    {
        var csv = new StringBuilder("ExternalId,InvoiceNumber,Amount,Currency\n");

        for (int index = 0; index < ImportLimits.MaxDataRows + 1; index++)
        {
            csv.Append("INV-").Append(index).Append(",FV,1.00,PLN\n");
        }

        Assert.Equal(
            ReconciliationApplicationErrors.ImportTooManyRows.Code,
            _reader.Read(
                ImportType.Invoice,
                Encoding.UTF8.GetBytes(csv.ToString()),
                CancellationToken.None).Error!.Code);
    }

    private ParsedCsvFile Read(ImportType importType, string csv)
    {
        return ReadBytes(importType, Encoding.UTF8.GetBytes(csv));
    }

    private ParsedCsvFile ReadBytes(ImportType importType, byte[] bytes)
    {
        var result = _reader.Read(importType, bytes, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Code);

        return result.Value;
    }
}