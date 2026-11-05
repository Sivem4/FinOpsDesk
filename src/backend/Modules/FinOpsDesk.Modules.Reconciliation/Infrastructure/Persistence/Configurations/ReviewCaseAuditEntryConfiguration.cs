using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Conversions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Configurations;

internal sealed class ReviewCaseAuditEntryConfiguration : IEntityTypeConfiguration<ReviewCaseAuditEntry>
{
    internal const string TriggerName = "TR_ReviewCaseAuditEntries_RejectMutation";

    internal const string ActionUniqueIndexName = "UX_ReviewCaseAuditEntries_OrganizationId_ReviewCaseId_Action";

    internal const string TerminalUniqueIndexName = "UX_ReviewCaseAuditEntries_OrganizationId_ReviewCaseId_Terminal";

    internal const string ActionCheckName = "CK_ReviewCaseAuditEntries_Action";

    internal const string ActorIdCheckName = "CK_ReviewCaseAuditEntries_ActorId";

    internal const string OpenedShapeCheckName = "CK_ReviewCaseAuditEntries_OpenedShape";

    internal const string ResolvedShapeCheckName = "CK_ReviewCaseAuditEntries_ResolvedShape";

    internal const string DismissedShapeCheckName = "CK_ReviewCaseAuditEntries_DismissedShape";

    internal static readonly string ActionCheckSql =
        "([Action] IN ("
        + (int)ReviewCaseAuditAction.Opened
        + ", "
        + (int)ReviewCaseAuditAction.Resolved
        + ", "
        + (int)ReviewCaseAuditAction.Dismissed
        + "))";

    // DATALENGTH counts trailing spaces; LEN and SQL equality do not.
    // LTRIM/RTRIM remove only U+0020. nvarchar DATALENGTH is two bytes per UTF-16 code unit.
    // A CHECK passes when its predicate is UNKNOWN, so every nullable column is compared with IS NULL.
    internal static readonly string ActorIdCheckSql =
        "DATALENGTH([ActorId]) = DATALENGTH(LTRIM(RTRIM([ActorId]))) AND DATALENGTH([ActorId]) BETWEEN 2 AND "
        + (ReviewCaseAuditEntry.MaxActorIdLength * 2);

    // A comparison with NULL is UNKNOWN, and SQL Server accepts UNKNOWN in a CHECK.
    // Required values therefore use IS NOT NULL before IN, so a missing value is FALSE.
    // BIN2 matches ReviewCaseSource.Parse. A case-insensitive database collation would
    // otherwise accept MANUAL and then fail when EF reads the row.
    internal static readonly string SourceLabelCheckSql =
        "([SourceLabel] IS NULL OR [SourceLabel] COLLATE Latin1_General_100_BIN2 IN (N'"
        + ReviewCaseSource.Manual.Value
        + "', N'"
        + ReviewCaseSource.Exact1.Value
        + "', N'"
        + ReviewCaseSource.Smart1.Value
        + "'))";

    internal static readonly string OpenedShapeCheckSql =
        "([Action] <> " + (int)ReviewCaseAuditAction.Opened
        + " OR ([Reason] IS NOT NULL AND "
        + ReviewCaseConfiguration.ReasonShapeCheckSql
        + " AND [Outcome] IS NULL AND [AllocationId] IS NULL AND [ResolutionNote] IS NULL AND "
        + SourceLabelCheckSql
        + " AND "
        + OptionalTrimmedText("Note")
        + "))";

    internal static readonly string ResolvedShapeCheckSql =
        "([Action] <> " + (int)ReviewCaseAuditAction.Resolved
        + " OR ([Reason] IS NULL AND [InvoiceId] IS NULL AND [BankTransactionId] IS NULL AND [SourceLabel] IS NULL AND [Note] IS NULL AND [Outcome] IS NOT NULL AND [Outcome] IN ("
        + (int)ReviewCaseOutcome.AllocationRecorded
        + ", "
        + (int)ReviewCaseOutcome.NoActionRequired
        + ", "
        + (int)ReviewCaseOutcome.HandledOutside
        + ") AND (([Outcome] = "
        + (int)ReviewCaseOutcome.AllocationRecorded
        + " AND [AllocationId] IS NOT NULL) OR ([Outcome] IN ("
        + (int)ReviewCaseOutcome.NoActionRequired
        + ", "
        + (int)ReviewCaseOutcome.HandledOutside
        + ") AND [AllocationId] IS NULL)) AND "
        + OptionalTrimmedText("ResolutionNote")
        + "))";

    internal static readonly string DismissedShapeCheckSql =
        "([Action] <> " + (int)ReviewCaseAuditAction.Dismissed
        + " OR ([Reason] IS NULL AND [InvoiceId] IS NULL AND [BankTransactionId] IS NULL AND [SourceLabel] IS NULL AND [Note] IS NULL AND [Outcome] IS NULL AND [AllocationId] IS NULL AND "
        + OptionalTrimmedText("ResolutionNote")
        + "))";

    public void Configure(EntityTypeBuilder<ReviewCaseAuditEntry> builder)
    {
        builder.ToTable("ReviewCaseAuditEntries", table =>
        {
            table.HasTrigger(TriggerName);
            table.UseSqlOutputClause(false);
            table.HasCheckConstraint(ActionCheckName, ActionCheckSql);
            table.HasCheckConstraint(ActorIdCheckName, ActorIdCheckSql);
            table.HasCheckConstraint(OpenedShapeCheckName, OpenedShapeCheckSql);
            table.HasCheckConstraint(ResolvedShapeCheckName, ResolvedShapeCheckSql);
            table.HasCheckConstraint(DismissedShapeCheckName, DismissedShapeCheckSql);
        });

        builder.HasKey(entry => entry.Id);

        builder.Property(entry => entry.Id)
            .HasConversion(ReconciliationValueConverters.ReviewCaseAuditEntryId)
            .ValueGeneratedNever();

        builder.Property(entry => entry.OrganizationId)
            .HasConversion(ReconciliationValueConverters.OrganizationId);

        builder.Property(entry => entry.ReviewCaseId)
            .HasConversion(ReconciliationValueConverters.ReviewCaseId);

        builder.Property(entry => entry.Action)
            .HasConversion<int>();

        builder.Property(entry => entry.ActorId)
            .HasMaxLength(ReviewCaseAuditEntry.MaxActorIdLength)
            .IsRequired();

        builder.Property(entry => entry.RecordedAt)
            .HasColumnType("datetimeoffset");

        builder.Property(entry => entry.Reason)
            .HasConversion<int>();

        builder.Property(entry => entry.InvoiceId)
            .HasConversion(
                id => id == null ? (Guid?)null : id.Value,
                value => value.HasValue ? new InvoiceId(value.Value) : null);

        builder.Property(entry => entry.BankTransactionId)
            .HasConversion(
                id => id == null ? (Guid?)null : id.Value,
                value => value.HasValue ? new BankTransactionId(value.Value) : null);

        builder.Property(entry => entry.SourceLabel)
            .HasConversion(
                source => source == null ? null : source.Value,
                value => value == null ? null : ReviewCaseSource.Parse(value))
            .HasMaxLength(ReviewCaseSource.MaxLength);

        builder.Property(entry => entry.Note)
            .HasMaxLength(ReviewCase.MaxNoteLength);

        builder.Property(entry => entry.Outcome)
            .HasConversion<int>();

        builder.Property(entry => entry.AllocationId)
            .HasConversion(
                id => id == null ? (Guid?)null : id.Value,
                value => value.HasValue ? new AllocationId(value.Value) : null);

        builder.Property(entry => entry.ResolutionNote)
            .HasMaxLength(ReviewCase.MaxNoteLength);

        builder.HasIndex(entry => new
        {
            entry.OrganizationId,
            entry.ReviewCaseId,
            entry.Action,
        })
            .IsUnique()
            .HasDatabaseName(ActionUniqueIndexName);

        builder.HasIndex(entry => new
        {
            entry.OrganizationId,
            entry.ReviewCaseId,
        })
            .IsUnique()
            .HasFilter(
                "[Action] IN ("
                + (int)ReviewCaseAuditAction.Resolved
                + ", "
                + (int)ReviewCaseAuditAction.Dismissed
                + ")")
            .HasDatabaseName(TerminalUniqueIndexName);

        builder.HasOne<ReviewCase>()
            .WithMany()
            .HasForeignKey(entry => new
            {
                entry.OrganizationId,
                entry.ReviewCaseId,
            })
            .HasPrincipalKey(reviewCase => new
            {
                reviewCase.OrganizationId,
                reviewCase.Id,
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
            .OnDelete(DeleteBehavior.Restrict)
            .IsRequired(false);

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
            .OnDelete(DeleteBehavior.Restrict)
            .IsRequired(false);

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
            .OnDelete(DeleteBehavior.Restrict)
            .IsRequired(false);
    }

    private static string OptionalTrimmedText(string column)
    {
        return "([" + column + "] IS NULL OR (DATALENGTH([" + column + "]) = DATALENGTH(LTRIM(RTRIM(["
            + column + "]))) AND DATALENGTH([" + column + "]) BETWEEN 2 AND "
            + (ReviewCase.MaxNoteLength * 2) + "))";
    }
}