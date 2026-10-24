using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Imports;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.SharedKernel.Results;
using FinOpsDesk.SharedKernel.Tenancy;

namespace FinOpsDesk.Modules.Reconciliation.Application.Imports;

public static class ImportClassifier
{
    public static IReadOnlyList<ClassifiedImportRow<InvoiceImportFacts>> ClassifyInvoices(
        OrganizationId organizationId,
        SourceSystem sourceSystem,
        IReadOnlyList<PreparedImportRow<InvoiceImportFacts>> rows,
        IReadOnlyDictionary<string, InvoiceImportFacts> existing)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(sourceSystem);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(existing);

        return Classify(
            rows,
            existing,
            (authority, candidate) => EquivalentInvoice(
                organizationId,
                sourceSystem,
                authority,
                candidate));
    }

    public static IReadOnlyList<ClassifiedImportRow<BankTransactionImportFacts>> ClassifyBankTransactions(
        OrganizationId organizationId,
        SourceSystem sourceSystem,
        IReadOnlyList<PreparedImportRow<BankTransactionImportFacts>> rows,
        IReadOnlyDictionary<string, BankTransactionImportFacts> existing)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(sourceSystem);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(existing);

        return Classify(
            rows,
            existing,
            (authority, candidate) => EquivalentBankTransaction(
                organizationId,
                sourceSystem,
                authority,
                candidate));
    }

    public static Invoice CreateInvoice(
        OrganizationId organizationId,
        SourceSystem sourceSystem,
        InvoiceImportFacts facts)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(sourceSystem);
        ArgumentNullException.ThrowIfNull(facts);

        return new Invoice(
            InvoiceId.New(),
            organizationId,
            facts.InvoiceNumber,
            facts.TotalAmount,
            new SourceIdentity(sourceSystem, new ExternalId(facts.ExternalId)));
    }

    public static BankTransaction CreateBankTransaction(
        OrganizationId organizationId,
        SourceSystem sourceSystem,
        BankTransactionImportFacts facts)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(sourceSystem);
        ArgumentNullException.ThrowIfNull(facts);

        return new BankTransaction(
            BankTransactionId.New(),
            organizationId,
            facts.Amount,
            facts.BookingDate,
            facts.Reference,
            new SourceIdentity(sourceSystem, new ExternalId(facts.ExternalId)));
    }

    private static List<ClassifiedImportRow<TFacts>> Classify<TFacts>(
        IReadOnlyList<PreparedImportRow<TFacts>> rows,
        IReadOnlyDictionary<string, TFacts> existing,
        Func<TFacts, TFacts, bool> equivalent)
        where TFacts : class
    {
        var ledger = new Dictionary<string, TFacts>(StringComparer.Ordinal);
        var classified = new List<ClassifiedImportRow<TFacts>>(rows.Count);

        foreach (PreparedImportRow<TFacts> row in rows)
        {
            if (row.Facts is null || row.Error is not null || row.ExternalId is null)
            {
                classified.Add(new ClassifiedImportRow<TFacts>(
                    row.DataRowIndex,
                    row.SourceLineNumber,
                    ClassifiedImportKind.Rejected,
                    Facts: null,
                    row.Error ?? ImportRowErrors.InvalidExternalId,
                    row.ColumnName ?? "ExternalId"));
                continue;
            }

            if (ledger.TryGetValue(row.ExternalId, out TFacts? known))
            {
                classified.Add(Compare(row, known, equivalent));
                continue;
            }

            if (existing.TryGetValue(row.ExternalId, out TFacts? stored))
            {
                ledger.Add(row.ExternalId, stored);
                classified.Add(Compare(row, stored, equivalent));
                continue;
            }

            ledger.Add(row.ExternalId, row.Facts);
            classified.Add(new ClassifiedImportRow<TFacts>(
                row.DataRowIndex,
                row.SourceLineNumber,
                ClassifiedImportKind.Imported,
                row.Facts,
                Error: null,
                ColumnName: null));
        }

        return classified;
    }

    private static ClassifiedImportRow<TFacts> Compare<TFacts>(
        PreparedImportRow<TFacts> row,
        TFacts authority,
        Func<TFacts, TFacts, bool> equivalent)
        where TFacts : class
    {
        if (equivalent(authority, row.Facts!))
        {
            return new ClassifiedImportRow<TFacts>(
                row.DataRowIndex,
                row.SourceLineNumber,
                ClassifiedImportKind.AlreadyExists,
                Facts: null,
                Error: null,
                ColumnName: null);
        }

        return new ClassifiedImportRow<TFacts>(
            row.DataRowIndex,
            row.SourceLineNumber,
            ClassifiedImportKind.Rejected,
            Facts: null,
            ImportRowErrors.SourceConflict,
            "ExternalId");
    }

    private static bool EquivalentInvoice(
        OrganizationId organizationId,
        SourceSystem sourceSystem,
        InvoiceImportFacts authority,
        InvoiceImportFacts candidate)
    {
        Invoice invoice = CreateInvoice(organizationId, sourceSystem, authority);

        return invoice.HasEquivalentImportedBusinessData(
            candidate.InvoiceNumber,
            candidate.TotalAmount);
    }

    private static bool EquivalentBankTransaction(
        OrganizationId organizationId,
        SourceSystem sourceSystem,
        BankTransactionImportFacts authority,
        BankTransactionImportFacts candidate)
    {
        BankTransaction transaction = CreateBankTransaction(
            organizationId,
            sourceSystem,
            authority);

        return transaction.HasEquivalentImportedBusinessData(
            candidate.Amount,
            candidate.BookingDate,
            candidate.Reference);
    }
}