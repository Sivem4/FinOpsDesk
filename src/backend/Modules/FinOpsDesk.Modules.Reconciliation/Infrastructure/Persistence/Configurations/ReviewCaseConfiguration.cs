using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Conversions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Configurations;

internal sealed class ReviewCaseConfiguration : IEntityTypeConfiguration<ReviewCase>
{
    internal const string ClosedAtCheckName = "CK_ReviewCases_ClosedAt";

    internal const string ReasonShapeCheckName = "CK_ReviewCases_ReasonShape";

    internal const string StatusShapeCheckName = "CK_ReviewCases_StatusShape";

    internal const string SourceLabelCheckName = "CK_ReviewCases_SourceLabel";

    internal const string AssignmentCheckName = "CK_ReviewCases_Assignment";

    internal const string ClosedAtCheckSql =
        "[ClosedAt] IS NULL OR [ClosedAt] >= [OpenedAt]";

    internal const string OrganizationAlternateKeyName = "AK_ReviewCases_OrganizationId_Id";

    internal const string OpenSubjectIndexName = "UX_ReviewCases_OrganizationId_SubjectKey_Open";

    internal static readonly string ReasonShapeCheckSql =
        "([Reason] IN ("
        + $"{(int)ReviewCaseReason.CompetingMatch}, {(int)ReviewCaseReason.NeedsInvestigation})) "
        + "AND (([Reason] = "
        + $"{(int)ReviewCaseReason.CompetingMatch} "
        + "AND [InvoiceId] IS NOT NULL AND [BankTransactionId] IS NOT NULL) "
        + "OR ([Reason] = "
        + $"{(int)ReviewCaseReason.NeedsInvestigation} "
        + "AND (([InvoiceId] IS NOT NULL AND [BankTransactionId] IS NULL) "
        + "OR ([InvoiceId] IS NULL AND [BankTransactionId] IS NOT NULL))))";

    internal static readonly string StatusShapeCheckSql =
        "([Status] IN ("
        + $"{(int)ReviewCaseStatus.Open}, {(int)ReviewCaseStatus.Resolved}, {(int)ReviewCaseStatus.Dismissed})) "
        + "AND ([Outcome] IS NULL OR [Outcome] IN ("
        + $"{(int)ReviewCaseOutcome.AllocationRecorded}, {(int)ReviewCaseOutcome.NoActionRequired}, {(int)ReviewCaseOutcome.HandledOutside})) "
        + "AND (([Status] = "
        + $"{(int)ReviewCaseStatus.Open} "
        + "AND [Outcome] IS NULL AND [AllocationId] IS NULL AND [ResolutionNote] IS NULL AND [ClosedAt] IS NULL) "
        + "OR ([Status] = "
        + $"{(int)ReviewCaseStatus.Resolved} "
        + "AND [Outcome] IS NOT NULL AND [ClosedAt] IS NOT NULL AND (("
        + $"[Outcome] = {(int)ReviewCaseOutcome.AllocationRecorded} AND [AllocationId] IS NOT NULL) "
        + "OR ([Outcome] IN ("
        + $"{(int)ReviewCaseOutcome.NoActionRequired}, {(int)ReviewCaseOutcome.HandledOutside}) "
        + "AND [AllocationId] IS NULL))) "
        + "OR ([Status] = "
        + $"{(int)ReviewCaseStatus.Dismissed} "
        + "AND [Outcome] IS NULL AND [AllocationId] IS NULL AND [ClosedAt] IS NOT NULL))";

    internal static readonly string AssignmentCheckSql =
        "(([AssignedActorSource] IS NULL AND [AssignedActorId] IS NULL AND [AssignedAt] IS NULL) "
        + "OR ([AssignedActorSource] IS NOT NULL AND [AssignedActorSource] IN ("
        + $"{(int)ReviewCaseActorSource.DevelopmentJwt}, {(int)ReviewCaseActorSource.ProductionCookie}) "
        + "AND [AssignedActorId] IS NOT NULL "
        + "AND DATALENGTH([AssignedActorId]) = DATALENGTH(LTRIM(RTRIM([AssignedActorId]))) "
        + "AND DATALENGTH([AssignedActorId]) BETWEEN 2 AND "
        + (ReviewCaseAssignmentEntry.MaxActorIdLength * 2)
        + " AND [AssignedAt] IS NOT NULL))";

    internal static readonly string SourceLabelCheckSql =
        "([SourceLabel] IS NULL OR [SourceLabel] IN (N'"
        + ReviewCaseSource.Manual.Value
        + "', N'"
        + ReviewCaseSource.Exact1.Value
        + "', N'"
        + ReviewCaseSource.Smart1.Value
        + "'))";

    public void Configure(EntityTypeBuilder<ReviewCase> builder)
    {
        builder.ToTable("ReviewCases", table =>
        {
            table.HasCheckConstraint(ClosedAtCheckName, ClosedAtCheckSql);
            table.HasCheckConstraint(ReasonShapeCheckName, ReasonShapeCheckSql);
            table.HasCheckConstraint(StatusShapeCheckName, StatusShapeCheckSql);
            table.HasCheckConstraint(SourceLabelCheckName, SourceLabelCheckSql);
            table.HasCheckConstraint(AssignmentCheckName, AssignmentCheckSql);
        });

        builder.HasKey(reviewCase => reviewCase.Id);

        builder.HasAlternateKey(reviewCase => new
        {
            reviewCase.OrganizationId,
            reviewCase.Id,
        })
            .HasName(OrganizationAlternateKeyName);

        builder.Property(reviewCase => reviewCase.Id)
            .HasConversion(ReconciliationValueConverters.ReviewCaseId)
            .ValueGeneratedNever();

        builder.Property(reviewCase => reviewCase.OrganizationId)
            .HasConversion(ReconciliationValueConverters.OrganizationId);

        builder.Property(reviewCase => reviewCase.Reason)
            .HasConversion<int>();

        builder.Property(reviewCase => reviewCase.Status)
            .HasConversion<int>();

        builder.Property(reviewCase => reviewCase.InvoiceId)
            .HasConversion(
                id => id == null ? (Guid?)null : id.Value,
                value => value.HasValue ? new InvoiceId(value.Value) : null);

        builder.Property(reviewCase => reviewCase.BankTransactionId)
            .HasConversion(
                id => id == null ? (Guid?)null : id.Value,
                value => value.HasValue ? new BankTransactionId(value.Value) : null);

        builder.Property(reviewCase => reviewCase.SubjectKey)
            .HasMaxLength(ReviewCase.SubjectKeyMaxLength)
            .IsRequired();

        builder.Property(reviewCase => reviewCase.SourceLabel)
            .HasConversion(
                source => source == null ? null : source.Value,
                value => value == null ? null : ReviewCaseSource.Parse(value))
            .HasMaxLength(ReviewCaseSource.MaxLength);

        builder.Property(reviewCase => reviewCase.Note)
            .HasMaxLength(ReviewCase.MaxNoteLength);

        builder.Property(reviewCase => reviewCase.Outcome)
            .HasConversion<int>();

        builder.Property(reviewCase => reviewCase.ResolutionNote)
            .HasMaxLength(ReviewCase.MaxNoteLength);

        builder.Property(reviewCase => reviewCase.AllocationId)
            .HasConversion(
                id => id == null ? (Guid?)null : id.Value,
                value => value.HasValue ? new AllocationId(value.Value) : null);

        builder.Property(reviewCase => reviewCase.OpenedAt)
            .HasColumnType("datetimeoffset");

        builder.Property(reviewCase => reviewCase.ClosedAt)
            .HasColumnType("datetimeoffset");

        builder.Property(reviewCase => reviewCase.AssignedActorSource)
            .HasConversion<int>();

        builder.Property(reviewCase => reviewCase.AssignedActorId)
            .HasMaxLength(ReviewCaseAssignmentEntry.MaxActorIdLength);

        builder.Property(reviewCase => reviewCase.AssignedAt)
            .HasColumnType("datetimeoffset");

        builder.Property<byte[]>("RowVersion")
            .IsRowVersion()
            .IsRequired();

        builder.HasIndex(reviewCase => new
        {
            reviewCase.OrganizationId,
            reviewCase.SubjectKey,
        })
            .IsUnique()
            .HasFilter($"[Status] = {(int)ReviewCaseStatus.Open}")
            .HasDatabaseName(OpenSubjectIndexName);

        // Invoice and BankTransaction expose (OrganizationId, Id). Allocation now has that
        // alternate key for financial audit. Review cases still store AllocationId without
        // a composite foreign key. Phase 4F.2 checks that the allocation exists in the
        // case's organization.
        builder.HasOne<Invoice>()
            .WithMany()
            .HasForeignKey(reviewCase => new
            {
                reviewCase.OrganizationId,
                reviewCase.InvoiceId,
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
            .HasForeignKey(reviewCase => new
            {
                reviewCase.OrganizationId,
                reviewCase.BankTransactionId,
            })
            .HasPrincipalKey(bankTransaction => new
            {
                bankTransaction.OrganizationId,
                bankTransaction.Id,
            })
            .OnDelete(DeleteBehavior.Restrict)
            .IsRequired(false);
    }
}