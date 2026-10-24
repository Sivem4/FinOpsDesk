using FinOpsDesk.Modules.Reconciliation.Application.Imports;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Imports;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.UnitTests.Reconciliation.Application.Imports;

public sealed class ImportClassifierTests
{
    private static readonly OrganizationId Organization = OrganizationId.New();
    private static readonly SourceSystem Source = new("mBank");
    private static readonly CurrencyCode Pln = new("PLN");

    [Fact]
    public void Classifies_new_equivalent_and_conflicting_rows_in_one_file()
    {
        PreparedImportRow<InvoiceImportFacts>[] rows =
        [
            Valid(1, "INV-001", "FV/001", 1000m),
            Valid(2, "INV-002", "FV/002", 500m),
            Valid(3, "INV-001", "FV/001", 1000m),
            Valid(4, "INV-001", "FV/001", 1200m)
        ];

        IReadOnlyList<ClassifiedImportRow<InvoiceImportFacts>> classified =
            ImportClassifier.ClassifyInvoices(Organization, Source, rows, Empty());

        Assert.Equal(ClassifiedImportKind.Imported, classified[0].Kind);
        Assert.Equal(ClassifiedImportKind.Imported, classified[1].Kind);
        Assert.Equal(ClassifiedImportKind.AlreadyExists, classified[2].Kind);
        Assert.Equal(ClassifiedImportKind.Rejected, classified[3].Kind);
        Assert.Equal(ImportRowErrors.SourceConflict.Code, classified[3].Error!.Code);
    }

    [Fact]
    public void Invalid_first_occurrence_does_not_reserve_the_identity()
    {
        PreparedImportRow<InvoiceImportFacts>[] rows =
        [
            Invalid(1, "INV-007"),
            Valid(2, "INV-007", "FV/007", 10m),
            Valid(3, "INV-007", "FV/007", 12m)
        ];

        IReadOnlyList<ClassifiedImportRow<InvoiceImportFacts>> classified =
            ImportClassifier.ClassifyInvoices(Organization, Source, rows, Empty());

        Assert.Equal(ClassifiedImportKind.Rejected, classified[0].Kind);
        Assert.Equal(ClassifiedImportKind.Imported, classified[1].Kind);
        Assert.Equal(ClassifiedImportKind.Rejected, classified[2].Kind);
        Assert.Equal(ImportRowErrors.SourceConflict.Code, classified[2].Error!.Code);
    }

    [Fact]
    public void Database_facts_win_over_a_conflicting_first_row()
    {
        var existing = new Dictionary<string, InvoiceImportFacts>(StringComparer.Ordinal)
        {
            ["INV-001"] = new("INV-001", "FV/001", new Money(1000m, Pln))
        };
        PreparedImportRow<InvoiceImportFacts>[] rows =
        [
            Valid(1, "INV-001", "FV/001", 1200m),
            Valid(2, "INV-001", "FV/001", 1000m),
            Valid(3, "INV-001", "FV/002", 1000m)
        ];

        IReadOnlyList<ClassifiedImportRow<InvoiceImportFacts>> classified =
            ImportClassifier.ClassifyInvoices(Organization, Source, rows, existing);

        Assert.Equal(ClassifiedImportKind.Rejected, classified[0].Kind);
        Assert.Equal(ClassifiedImportKind.AlreadyExists, classified[1].Kind);
        Assert.Equal(ClassifiedImportKind.Rejected, classified[2].Kind);
    }

    [Fact]
    public void External_id_comparison_is_case_sensitive()
    {
        PreparedImportRow<InvoiceImportFacts>[] rows =
        [
            Valid(1, "abc", "FV/1", 10m),
            Valid(2, "ABC", "FV/2", 20m)
        ];

        IReadOnlyList<ClassifiedImportRow<InvoiceImportFacts>> classified =
            ImportClassifier.ClassifyInvoices(Organization, Source, rows, Empty());

        Assert.All(classified, row => Assert.Equal(ClassifiedImportKind.Imported, row.Kind));
    }

    [Fact]
    public void Allocated_amount_is_not_part_of_imported_equivalence()
    {
        Invoice stored = ImportClassifier.CreateInvoice(
            Organization,
            Source,
            new InvoiceImportFacts("INV-001", "FV/001", new Money(1000m, Pln)));
        Assert.True(stored.Allocate(new Money(10m, Pln)).IsSuccess);

        var existing = new Dictionary<string, InvoiceImportFacts>(StringComparer.Ordinal)
        {
            ["INV-001"] = new("INV-001", stored.InvoiceNumber, stored.TotalAmount)
        };

        IReadOnlyList<ClassifiedImportRow<InvoiceImportFacts>> classified =
            ImportClassifier.ClassifyInvoices(
                Organization,
                Source,
                [Valid(1, "INV-001", "FV/001", 1000m)],
                existing);

        Assert.Equal(ClassifiedImportKind.AlreadyExists, Assert.Single(classified).Kind);
        Assert.Equal(10m, stored.AllocatedAmount.Amount);
    }

    [Fact]
    public void Classifies_bank_transactions_with_normalized_references()
    {
        PreparedImportRow<BankTransactionImportFacts>[] rows =
        [
            Bank(1, "TXN-1", 10m, " REF "),
            Bank(2, "TXN-1", 10m, "REF"),
            Bank(3, "TXN-1", 11m, "REF")
        ];

        IReadOnlyList<ClassifiedImportRow<BankTransactionImportFacts>> classified =
            ImportClassifier.ClassifyBankTransactions(Organization, Source, rows, EmptyBank());

        Assert.Equal(ClassifiedImportKind.Imported, classified[0].Kind);
        Assert.Equal(ClassifiedImportKind.AlreadyExists, classified[1].Kind);
        Assert.Equal(ClassifiedImportKind.Rejected, classified[2].Kind);
    }

    private static PreparedImportRow<InvoiceImportFacts> Valid(
        int index,
        string externalId,
        string invoiceNumber,
        decimal amount)
    {
        return new PreparedImportRow<InvoiceImportFacts>(
            index,
            index + 1,
            externalId,
            new InvoiceImportFacts(externalId, invoiceNumber, new Money(amount, Pln)),
            Error: null,
            ColumnName: null);
    }

    private static PreparedImportRow<InvoiceImportFacts> Invalid(int index, string externalId)
    {
        return new PreparedImportRow<InvoiceImportFacts>(
            index,
            index + 1,
            externalId,
            Facts: null,
            ImportRowErrors.InvalidInvoiceNumber,
            "InvoiceNumber");
    }

    private static PreparedImportRow<BankTransactionImportFacts> Bank(
        int index,
        string externalId,
        decimal amount,
        string reference)
    {
        return new PreparedImportRow<BankTransactionImportFacts>(
            index,
            index + 1,
            externalId,
            new BankTransactionImportFacts(
                externalId,
                new Money(amount, Pln),
                new DateOnly(2026, 8, 24),
                reference),
            Error: null,
            ColumnName: null);
    }

    private static Dictionary<string, InvoiceImportFacts> Empty()
    {
        return new Dictionary<string, InvoiceImportFacts>(StringComparer.Ordinal);
    }

    private static Dictionary<string, BankTransactionImportFacts> EmptyBank()
    {
        return new Dictionary<string, BankTransactionImportFacts>(StringComparer.Ordinal);
    }
}