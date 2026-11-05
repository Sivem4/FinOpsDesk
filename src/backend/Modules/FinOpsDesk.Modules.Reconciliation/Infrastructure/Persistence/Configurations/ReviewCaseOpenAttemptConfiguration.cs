using FinOpsDesk.Modules.Reconciliation.Domain.BankTransactions;
using FinOpsDesk.Modules.Reconciliation.Domain.Invoices;
using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Conversions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Configurations;

internal sealed class ReviewCaseOpenAttemptConfiguration : IEntityTypeConfiguration<ReviewCaseOpenAttempt>
{
    internal const string PrimaryKeyName = "PK_ReviewCaseOpenAttempts";

    internal const string ReasonShapeCheckName = "CK_ReviewCaseOpenAttempts_ReasonShape";

    internal const string SourceLabelCheckName = "CK_ReviewCaseOpenAttempts_SourceLabel";

    internal const string IdempotencyKeyCheckName = "CK_ReviewCaseOpenAttempts_IdempotencyKey";

    internal const string ReviewCaseForeignKeyName =
        "FK_ReviewCaseOpenAttempts_ReviewCases_OrganizationId_ReviewCaseId";

    internal const string IdempotencyKeyCheckSql =
        "[IdempotencyKey] <> '00000000-0000-0000-0000-000000000000'";

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

    public void Configure(EntityTypeBuilder<ReviewCaseOpenAttempt> builder)
    {
        builder.ToTable("ReviewCaseOpenAttempts", table =>
        {
            table.HasCheckConstraint(ReasonShapeCheckName, ReviewCaseConfiguration.ReasonShapeCheckSql);
            table.HasCheckConstraint(SourceLabelCheckName, SourceLabelCheckSql);
            table.HasCheckConstraint(IdempotencyKeyCheckName, IdempotencyKeyCheckSql);
        });

        builder.HasKey(attempt => new
        {
            attempt.OrganizationId,
            attempt.IdempotencyKey,
        })
            .HasName(PrimaryKeyName);

        builder.Property(attempt => attempt.OrganizationId)
            .HasConversion(ReconciliationValueConverters.OrganizationId);

        builder.Property(attempt => attempt.IdempotencyKey);

        builder.Property(attempt => attempt.ReviewCaseId)
            .HasConversion(ReconciliationValueConverters.ReviewCaseId);

        builder.Property(attempt => attempt.Reason)
            .HasConversion<int>();

        builder.Property(attempt => attempt.InvoiceId)
            .HasConversion(
                id => id == null ? (Guid?)null : id.Value,
                value => value.HasValue ? new InvoiceId(value.Value) : null);

        builder.Property(attempt => attempt.BankTransactionId)
            .HasConversion(
                id => id == null ? (Guid?)null : id.Value,
                value => value.HasValue ? new BankTransactionId(value.Value) : null);

        builder.Property(attempt => attempt.SourceLabel)
            .HasConversion(
                source => source == null ? null : source.Value,
                value => value == null ? null : ReviewCaseSource.Parse(value))
            .HasMaxLength(ReviewCaseSource.MaxLength);

        builder.Property(attempt => attempt.Note)
            .HasMaxLength(ReviewCase.MaxNoteLength);

        builder.HasOne<ReviewCase>()
            .WithMany()
            .HasForeignKey(attempt => new
            {
                attempt.OrganizationId,
                attempt.ReviewCaseId,
            })
            .HasPrincipalKey(reviewCase => new
            {
                reviewCase.OrganizationId,
                reviewCase.Id,
            })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName(ReviewCaseForeignKeyName);
    }
}