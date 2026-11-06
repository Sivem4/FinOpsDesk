using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Conversions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Configurations;

internal sealed class ReviewCaseAssignmentEntryConfiguration : IEntityTypeConfiguration<ReviewCaseAssignmentEntry>
{
    internal const string TriggerName = "TR_ReviewCaseAssignmentEntries_RejectMutation";

    internal const string IdempotencyKeyIndexName = "UX_ReviewCaseAssignmentEntries_OrganizationId_IdempotencyKey";

    internal const string IdempotencyKeyCheckName = "CK_ReviewCaseAssignmentEntries_IdempotencyKey";

    internal const string OperationCheckName = "CK_ReviewCaseAssignmentEntries_Operation";

    internal const string ActorSourceCheckName = "CK_ReviewCaseAssignmentEntries_ActorSource";

    internal const string ActorIdCheckName = "CK_ReviewCaseAssignmentEntries_ActorId";

    internal const string TokenCheckName = "CK_ReviewCaseAssignmentEntries_RequestConcurrencyToken";

    internal const string AssignedAtCheckName = "CK_ReviewCaseAssignmentEntries_AssignedAt";

    internal static readonly string IdempotencyKeyCheckSql =
        "[IdempotencyKey] <> '00000000-0000-0000-0000-000000000000'";

    internal static readonly string OperationCheckSql =
        "[Operation] IN ("
        + $"{(int)ReviewCaseAssignmentOperation.Claim}, {(int)ReviewCaseAssignmentOperation.Release})";

    internal static readonly string ActorSourceCheckSql =
        "[ActorSource] IN ("
        + $"{(int)ReviewCaseActorSource.DevelopmentJwt}, {(int)ReviewCaseActorSource.ProductionCookie})";

    internal static readonly string ActorIdCheckSql =
        "DATALENGTH([ActorId]) = DATALENGTH(LTRIM(RTRIM([ActorId]))) AND DATALENGTH([ActorId]) BETWEEN 2 AND "
        + (ReviewCaseAssignmentEntry.MaxActorIdLength * 2);

    internal static readonly string TokenCheckSql =
        "DATALENGTH([RequestConcurrencyToken]) = DATALENGTH(LTRIM(RTRIM([RequestConcurrencyToken]))) "
        + "AND DATALENGTH([RequestConcurrencyToken]) = "
        + (ReviewCaseAssignmentEntry.CanonicalTokenLength * 2);

    internal static readonly string AssignedAtCheckSql =
        "(([Operation] = "
        + $"{(int)ReviewCaseAssignmentOperation.Claim} AND [AssignedAt] IS NOT NULL) OR ([Operation] = "
        + $"{(int)ReviewCaseAssignmentOperation.Release} AND [AssignedAt] IS NULL))";

    public void Configure(EntityTypeBuilder<ReviewCaseAssignmentEntry> builder)
    {
        builder.ToTable("ReviewCaseAssignmentEntries", table =>
        {
            table.HasTrigger(TriggerName);
            table.UseSqlOutputClause(false);
            table.HasCheckConstraint(IdempotencyKeyCheckName, IdempotencyKeyCheckSql);
            table.HasCheckConstraint(OperationCheckName, OperationCheckSql);
            table.HasCheckConstraint(ActorSourceCheckName, ActorSourceCheckSql);
            table.HasCheckConstraint(ActorIdCheckName, ActorIdCheckSql);
            table.HasCheckConstraint(TokenCheckName, TokenCheckSql);
            table.HasCheckConstraint(AssignedAtCheckName, AssignedAtCheckSql);
        });

        builder.HasKey(entry => entry.Id);

        builder.Property(entry => entry.Id)
            .HasConversion(ReconciliationValueConverters.ReviewCaseAssignmentEntryId)
            .ValueGeneratedNever();

        builder.Property(entry => entry.OrganizationId)
            .HasConversion(ReconciliationValueConverters.OrganizationId);

        builder.Property(entry => entry.ReviewCaseId)
            .HasConversion(ReconciliationValueConverters.ReviewCaseId);

        builder.Property(entry => entry.Operation)
            .HasConversion<int>();

        builder.Property(entry => entry.ActorSource)
            .HasConversion<int>();

        builder.Property(entry => entry.ActorId)
            .HasMaxLength(ReviewCaseAssignmentEntry.MaxActorIdLength)
            .IsRequired();

        builder.Property(entry => entry.RequestConcurrencyToken)
            .HasMaxLength(ReviewCaseAssignmentEntry.CanonicalTokenLength)
            .IsRequired();

        builder.Property(entry => entry.RecordedAt)
            .HasColumnType("datetimeoffset");

        builder.Property(entry => entry.AssignedAt)
            .HasColumnType("datetimeoffset");

        builder.HasIndex(entry => new
        {
            entry.OrganizationId,
            entry.IdempotencyKey,
        })
            .IsUnique()
            .HasDatabaseName(IdempotencyKeyIndexName);

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
    }
}