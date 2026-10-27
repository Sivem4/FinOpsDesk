using FinOpsDesk.Modules.Reconciliation.Domain;
using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.BatchExecutions;
using FinOpsDesk.Modules.Reconciliation.Domain.Imports;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Conversions;

internal static class ReconciliationValueConverters
{
    public static ValueConverter<InvoiceId, Guid> InvoiceId { get; } =
        new(id => id.Value, value => new InvoiceId(value));

    public static ValueConverter<BankTransactionId, Guid> BankTransactionId { get; } =
        new(id => id.Value, value => new BankTransactionId(value));

    public static ValueConverter<AllocationId, Guid> AllocationId { get; } =
        new(id => id.Value, value => new AllocationId(value));

    public static ValueConverter<ImportBatchId, Guid> ImportBatchId { get; } =
        new(id => id.Value, value => new ImportBatchId(value));

    public static ValueConverter<ReviewCaseId, Guid> ReviewCaseId { get; } =
        new(id => id.Value, value => new ReviewCaseId(value));

    public static ValueConverter<BatchExecutionId, Guid> BatchExecutionId { get; } =
        new(id => id.Value, value => new BatchExecutionId(value));

    public static ValueConverter<BatchExecutionFingerprint, byte[]> BatchExecutionFingerprint { get; } =
        new(
            fingerprint => fingerprint.ToArray(),
            value => new BatchExecutionFingerprint(value));

    public static ValueConverter<BatchExecutionStatus, int> BatchExecutionStatus { get; } =
        new(
            status => BatchExecutionStatusCodec.ToInt(status),
            value => BatchExecutionStatusCodec.ToStatus(value));

    public static ValueConverter<OrganizationId, Guid> OrganizationId { get; } =
        new(id => id.Value, value => new OrganizationId(value));

    public static ValueConverter<CurrencyCode, string> CurrencyCode { get; } =
        new(code => code.Value, value => new CurrencyCode(value));

    public static ValueConverter<SourceSystem, string> SourceSystem { get; } =
        new(system => system.Value, value => new SourceSystem(value));

    public static ValueConverter<ExternalId, string> ExternalId { get; } =
        new(id => id.Value, value => new ExternalId(value));

    public static ValueConverter<FileHash, string> FileHash { get; } =
        new(hash => hash.Value, value => new FileHash(value));
}