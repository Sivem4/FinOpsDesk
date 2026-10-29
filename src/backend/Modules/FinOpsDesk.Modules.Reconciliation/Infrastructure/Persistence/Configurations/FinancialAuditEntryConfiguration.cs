using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.BatchExecutions;
using FinOpsDesk.Modules.Reconciliation.Domain.FinancialAudit;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Conversions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Configurations;

internal sealed class FinancialAuditEntryConfiguration : IEntityTypeConfiguration<FinancialAuditEntry>
{
    internal const string TriggerName = "TR_FinancialAuditEntries_RejectMutation";

    internal const string UniqueIndexName = "UX_FinancialAuditEntries_OrganizationId_AllocationId_Action";

    internal const string ActionCheckName = "CK_FinancialAuditEntries_Action";

    internal const string ActorIdCheckName = "CK_FinancialAuditEntries_ActorId";

    internal const string AmountCheckName = "CK_FinancialAuditEntries_Amount";

    internal const string IdempotencyOperationCheckName = "CK_FinancialAuditEntries_IdempotencyOperation";

    internal const string BatchReferenceCheckName = "CK_FinancialAuditEntries_BatchReference";

    internal const string ActionOperationCheckName = "CK_FinancialAuditEntries_ActionOperation";

    internal const string ReasonCheckName = "CK_FinancialAuditEntries_Reason";

    internal static readonly string ActionCheckSql =
        "([Action] IN ("
        + (int)FinancialAuditAction.Confirmation
        + ", "
        + (int)FinancialAuditAction.Reversal
        + "))";

    // DATALENGTH counts trailing spaces; LEN and SQL equality do not, including under a binary collation.
    // LTRIM/RTRIM remove only U+0020. nvarchar DATALENGTH is two bytes per UTF-16 code unit.
    internal static readonly string ActorIdCheckSql =
        "DATALENGTH([ActorId]) = DATALENGTH(LTRIM(RTRIM([ActorId]))) AND DATALENGTH([ActorId]) BETWEEN 2 AND "
        + (FinancialAuditEntry.MaxActorIdLength * 2);

    internal const string AmountCheckSql = "([Amount] > 0)";

    internal static readonly string IdempotencyOperationCheckSql =
        "([IdempotencyOperation] IN ("
        + (int)AllocationIdempotencyOperation.ExactMatch
        + ", "
        + (int)AllocationIdempotencyOperation.ExplicitAmount
        + ", "
        + (int)AllocationIdempotencyOperation.BatchExactMatch
        + ", "
        + (int)AllocationIdempotencyOperation.Reversal
        + "))";

    internal static readonly string ActionOperationCheckSql =
        "(([Action] = " + (int)FinancialAuditAction.Confirmation
        + " AND [IdempotencyOperation] IN ("
        + (int)AllocationIdempotencyOperation.ExactMatch
        + ", "
        + (int)AllocationIdempotencyOperation.ExplicitAmount
        + ", "
        + (int)AllocationIdempotencyOperation.BatchExactMatch
        + ")) OR ([Action] = " + (int)FinancialAuditAction.Reversal
        + " AND [IdempotencyOperation] = "
        + (int)AllocationIdempotencyOperation.Reversal
        + "))";

    internal static readonly string BatchReferenceCheckSql =
        "(([IdempotencyOperation] = " + (int)AllocationIdempotencyOperation.BatchExactMatch
        + " AND [BatchExecutionId] IS NOT NULL) OR ([IdempotencyOperation] IN ("
        + (int)AllocationIdempotencyOperation.ExactMatch
        + ", "
        + (int)AllocationIdempotencyOperation.ExplicitAmount
        + ", "
        + (int)AllocationIdempotencyOperation.Reversal
        + ") AND [BatchExecutionId] IS NULL))";

    internal static readonly string ReasonCheckSql =
        "(([Action] = " + (int)FinancialAuditAction.Confirmation
        + " AND [Reason] IS NULL) OR ([Action] = " + (int)FinancialAuditAction.Reversal
        + " AND [Reason] IS NOT NULL AND DATALENGTH([Reason]) = DATALENGTH(LTRIM(RTRIM([Reason]))) AND DATALENGTH([Reason]) BETWEEN 2 AND "
        + (Allocation.MaxReversalReasonLength * 2)
        + "))";

    public void Configure(EntityTypeBuilder<FinancialAuditEntry> builder)
    {
        builder.ToTable("FinancialAuditEntries", table =>
        {
            table.HasTrigger(TriggerName);
            table.UseSqlOutputClause(false);
            table.HasCheckConstraint(ActionCheckName, ActionCheckSql);
            table.HasCheckConstraint(ActorIdCheckName, ActorIdCheckSql);
            table.HasCheckConstraint(AmountCheckName, AmountCheckSql);
            table.HasCheckConstraint(IdempotencyOperationCheckName, IdempotencyOperationCheckSql);
            table.HasCheckConstraint(ActionOperationCheckName, ActionOperationCheckSql);
            table.HasCheckConstraint(BatchReferenceCheckName, BatchReferenceCheckSql);
            table.HasCheckConstraint(ReasonCheckName, ReasonCheckSql);
        });

        builder.HasKey(entry => entry.Id);

        builder.Property(entry => entry.Id)
            .HasConversion(ReconciliationValueConverters.FinancialAuditEntryId)
            .ValueGeneratedNever();

        builder.Property(entry => entry.OrganizationId)
            .HasConversion(ReconciliationValueConverters.OrganizationId);

        builder.Property(entry => entry.AllocationId)
            .HasConversion(ReconciliationValueConverters.AllocationId);

        builder.Property(entry => entry.InvoiceId)
            .HasConversion(ReconciliationValueConverters.InvoiceId);

        builder.Property(entry => entry.BankTransactionId)
            .HasConversion(ReconciliationValueConverters.BankTransactionId);

        builder.Property(entry => entry.BatchExecutionId)
            .HasConversion(
                id => id == null ? (Guid?)null : id.Value,
                value => value.HasValue ? new BatchExecutionId(value.Value) : null);

        builder.Property(entry => entry.Action)
            .HasConversion<int>();

        builder.Property(entry => entry.ActorId)
            .HasMaxLength(FinancialAuditEntry.MaxActorIdLength)
            .IsRequired();
        builder.Property(entry => entry.Reason)
            .HasMaxLength(Allocation.MaxReversalReasonLength);

        builder.Property(entry => entry.RecordedAt)
            .HasColumnType("datetimeoffset");

        builder.Property(entry => entry.IdempotencyOperation)
            .HasConversion<int>();

        builder.Property(entry => entry.IdempotencyKey);

        MoneyPropertyConfiguration.Configure(
            builder.ComplexProperty(entry => entry.Amount),
            "Amount",
            "Currency");

        builder.HasIndex(entry => new
        {
            entry.OrganizationId,
            entry.AllocationId,
            entry.Action,
        })
            .IsUnique()
            .HasDatabaseName(UniqueIndexName);

        // Each foreign key requires the referenced row to belong to OrganizationId.
        // InvoiceId and BankTransactionId are not required to equal the pair on Allocation.
        // FinancialAuditEntry.ForConfirmation copies that pair from the allocation.
        builder.HasOne<Allocation>()
            .WithMany()
            .HasForeignKey(entry => new
            {
                entry.OrganizationId,
                entry.AllocationId,
            })
            .HasPrincipalKey(allocation => new
            {
                allocation.OrganizationId,
                allocation.Id,
            })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Invoice>()
            .WithMany()
            .HasForeignKey(entry => new
            {
                entry.OrganizationId,
                entry.InvoiceId,
            })
            .HasPrincipalKey(invoice => new
            {
                invoice.OrganizationId,
                invoice.Id,
            })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<BankTransaction>()
            .WithMany()
            .HasForeignKey(entry => new
            {
                entry.OrganizationId,
                entry.BankTransactionId,
            })
            .HasPrincipalKey(bankTransaction => new
            {
                bankTransaction.OrganizationId,
                bankTransaction.Id,
            })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<BatchExecution>()
            .WithMany()
            .HasForeignKey(entry => new
            {
                entry.OrganizationId,
                entry.BatchExecutionId,
            })
            .HasPrincipalKey(execution => new
            {
                execution.OrganizationId,
                execution.Id,
            })
            .OnDelete(DeleteBehavior.Restrict)
            .IsRequired(false);
    }
}