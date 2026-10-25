using FinOpsDesk.Modules.Reconciliation.Application.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.Matching;

namespace FinOpsDesk.Modules.Reconciliation.Application.Matching;

public sealed record ExactMatchResponse(
    string AlgorithmVersion,
    IReadOnlyList<ExactMatchItemResponse> ExactMatches,
    IReadOnlyList<AmbiguousMatchGroupResponse> AmbiguousGroups,
    IReadOnlyList<ClassifiedMatchResponse> Unmatched,
    IReadOnlyList<ClassifiedMatchResponse> Ineligible)
{
    public static ExactMatchResponse From(ExactMatchBatchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return new ExactMatchResponse(
            result.AlgorithmVersion,
            MapExact(result.ExactMatches),
            MapAmbiguous(result.AmbiguousGroups),
            MapClassified(result.Unmatched),
            MapClassified(result.Ineligible));
    }

    private static IReadOnlyList<ExactMatchItemResponse> MapExact(
        IReadOnlyList<ExactMatch> matches)
    {
        ExactMatchItemResponse[] items = new ExactMatchItemResponse[matches.Count];

        for (int index = 0; index < matches.Count; index++)
        {
            ExactMatch match = matches[index];
            items[index] = new ExactMatchItemResponse(
                match.InvoiceId.Value,
                match.BankTransactionId.Value,
                ToMoney(match.Amount),
                match.NormalizedReference,
                match.Reason.ToString());
        }

        return Array.AsReadOnly(items);
    }

    private static IReadOnlyList<AmbiguousMatchGroupResponse> MapAmbiguous(
        IReadOnlyList<AmbiguousMatchGroup> groups)
    {
        AmbiguousMatchGroupResponse[] items = new AmbiguousMatchGroupResponse[groups.Count];

        for (int index = 0; index < groups.Count; index++)
        {
            AmbiguousMatchGroup group = groups[index];
            items[index] = new AmbiguousMatchGroupResponse(
                group.NormalizedReference,
                ToMoney(group.OpenAmount),
                group.Reason.ToString(),
                MapInvoiceIds(group.InvoiceIds),
                MapBankTransactionIds(group.BankTransactionIds));
        }

        return Array.AsReadOnly(items);
    }

    private static IReadOnlyList<ClassifiedMatchResponse> MapClassified(
        IReadOnlyList<ClassifiedMatchRecord> records)
    {
        ClassifiedMatchResponse[] items = new ClassifiedMatchResponse[records.Count];

        for (int index = 0; index < records.Count; index++)
        {
            ClassifiedMatchRecord record = records[index];
            items[index] = new ClassifiedMatchResponse(
                record.Classification.ToString(),
                record.Reason.ToString(),
                record.InvoiceId?.Value,
                record.BankTransactionId?.Value,
                record.NormalizedReference,
                ToMoney(record.OpenAmount));
        }

        return Array.AsReadOnly(items);
    }

    private static IReadOnlyList<Guid> MapInvoiceIds(IReadOnlyList<InvoiceId> invoiceIds)
    {
        Guid[] ids = new Guid[invoiceIds.Count];

        for (int index = 0; index < invoiceIds.Count; index++)
        {
            ids[index] = invoiceIds[index].Value;
        }

        return Array.AsReadOnly(ids);
    }

    private static IReadOnlyList<Guid> MapBankTransactionIds(
        IReadOnlyList<BankTransactionId> bankTransactionIds)
    {
        Guid[] ids = new Guid[bankTransactionIds.Count];

        for (int index = 0; index < bankTransactionIds.Count; index++)
        {
            ids[index] = bankTransactionIds[index].Value;
        }

        return Array.AsReadOnly(ids);
    }

    private static MoneyDto ToMoney(Money money)
    {
        return new MoneyDto(money.Amount, money.Currency.Value);
    }
}

public sealed record ExactMatchItemResponse(
    Guid InvoiceId,
    Guid BankTransactionId,
    MoneyDto Amount,
    string NormalizedReference,
    string Reason);

public sealed record AmbiguousMatchGroupResponse(
    string NormalizedReference,
    MoneyDto OpenAmount,
    string Reason,
    IReadOnlyList<Guid> InvoiceIds,
    IReadOnlyList<Guid> BankTransactionIds);

public sealed record ClassifiedMatchResponse(
    string Classification,
    string Reason,
    Guid? InvoiceId,
    Guid? BankTransactionId,
    string? NormalizedReference,
    MoneyDto OpenAmount);