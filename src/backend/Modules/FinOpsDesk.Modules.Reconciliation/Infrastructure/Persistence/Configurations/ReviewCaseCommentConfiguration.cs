using FinOpsDesk.Modules.Reconciliation.Domain.ReviewCases;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Conversions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Configurations;

internal sealed class ReviewCaseCommentConfiguration : IEntityTypeConfiguration<ReviewCaseComment>
{
    internal const string TriggerName = "TR_ReviewCaseComments_RejectMutation";

    internal const string IdempotencyKeyIndexName = "UX_ReviewCaseComments_OrganizationId_IdempotencyKey";

    internal const string IdempotencyKeyCheckName = "CK_ReviewCaseComments_IdempotencyKey";

    internal const string TextCheckName = "CK_ReviewCaseComments_Text";

    internal const string ActorIdCheckName = "CK_ReviewCaseComments_ActorId";

    internal static readonly string IdempotencyKeyCheckSql =
        "[IdempotencyKey] <> '00000000-0000-0000-0000-000000000000'";

    // DATALENGTH counts trailing spaces; LEN and SQL equality do not.
    // LTRIM/RTRIM remove only U+0020. nvarchar DATALENGTH is two bytes per UTF-16 code unit.
    internal static readonly string TextCheckSql =
        "DATALENGTH([Text]) = DATALENGTH(LTRIM(RTRIM([Text]))) AND DATALENGTH([Text]) BETWEEN 2 AND "
        + (ReviewCaseComment.MaxTextLength * 2);

    internal static readonly string ActorIdCheckSql =
        "DATALENGTH([ActorId]) = DATALENGTH(LTRIM(RTRIM([ActorId]))) AND DATALENGTH([ActorId]) BETWEEN 2 AND "
        + (ReviewCaseComment.MaxActorIdLength * 2);

    public void Configure(EntityTypeBuilder<ReviewCaseComment> builder)
    {
        builder.ToTable("ReviewCaseComments", table =>
        {
            table.HasTrigger(TriggerName);
            table.UseSqlOutputClause(false);
            table.HasCheckConstraint(IdempotencyKeyCheckName, IdempotencyKeyCheckSql);
            table.HasCheckConstraint(TextCheckName, TextCheckSql);
            table.HasCheckConstraint(ActorIdCheckName, ActorIdCheckSql);
        });

        builder.HasKey(comment => comment.Id);

        builder.Property(comment => comment.Id)
            .HasConversion(ReconciliationValueConverters.ReviewCaseCommentId)
            .ValueGeneratedNever();

        builder.Property(comment => comment.OrganizationId)
            .HasConversion(ReconciliationValueConverters.OrganizationId);

        builder.Property(comment => comment.ReviewCaseId)
            .HasConversion(ReconciliationValueConverters.ReviewCaseId);

        builder.Property(comment => comment.Text)
            .HasMaxLength(ReviewCaseComment.MaxTextLength)
            .IsRequired();

        builder.Property(comment => comment.ActorId)
            .HasMaxLength(ReviewCaseComment.MaxActorIdLength)
            .IsRequired();

        builder.Property(comment => comment.RecordedAt)
            .HasColumnType("datetimeoffset");

        builder.HasIndex(comment => new
        {
            comment.OrganizationId,
            comment.IdempotencyKey,
        })
            .IsUnique()
            .HasDatabaseName(IdempotencyKeyIndexName);

        builder.HasIndex(comment => new
        {
            comment.OrganizationId,
            comment.ReviewCaseId,
            comment.RecordedAt,
            comment.Id,
        });

        builder.HasOne<ReviewCase>()
            .WithMany()
            .HasForeignKey(comment => new
            {
                comment.OrganizationId,
                comment.ReviewCaseId,
            })
            .HasPrincipalKey(reviewCase => new
            {
                reviewCase.OrganizationId,
                reviewCase.Id,
            })
            .OnDelete(DeleteBehavior.Restrict);
    }
}