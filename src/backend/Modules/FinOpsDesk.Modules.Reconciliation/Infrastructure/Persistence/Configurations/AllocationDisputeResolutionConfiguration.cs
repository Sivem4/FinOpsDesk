using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Conversions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Configurations;

internal sealed class AllocationDisputeResolutionConfiguration : IEntityTypeConfiguration<AllocationDisputeResolution>
{
    internal const string TriggerName = "TR_AllocationDisputeResolutions_RejectMutation";

    internal const string PrimaryKeyName = "PK_AllocationDisputeResolutions";

    internal const string IdempotencyKeyIndexName = "UX_AllocationDisputeResolutions_OrganizationId_IdempotencyKey";

    internal const string ForeignKeyName =
        "FK_AllocationDisputeResolutions_AllocationDisputes_OrganizationId_AllocationDisputeId";

    internal const string IdempotencyKeyCheckName = "CK_AllocationDisputeResolutions_IdempotencyKey";

    internal const string OutcomeCheckName = "CK_AllocationDisputeResolutions_Outcome";

    internal const string ReasonCheckName = "CK_AllocationDisputeResolutions_Reason";

    internal const string ActorIdCheckName = "CK_AllocationDisputeResolutions_ActorId";

    internal const string ActorSourceCheckName = "CK_AllocationDisputeResolutions_ActorSource";

    internal const int ImmutabilityErrorNumber = 50004;

    internal static readonly string IdempotencyKeyCheckSql =
        "[IdempotencyKey] <> '00000000-0000-0000-0000-000000000000'";

    internal static readonly string OutcomeCheckSql = "[Outcome] IN (1, 2)";

    // DATALENGTH counts trailing spaces; LEN and SQL equality do not.
    // LTRIM/RTRIM remove only U+0020. nvarchar DATALENGTH is two bytes per UTF-16 code unit.
    internal static readonly string ReasonCheckSql =
        "DATALENGTH([Reason]) = DATALENGTH(LTRIM(RTRIM([Reason]))) AND DATALENGTH([Reason]) BETWEEN 2 AND "
        + (AllocationDisputeResolution.MaxReasonLength * 2);

    internal static readonly string ActorIdCheckSql =
        "DATALENGTH([ActorId]) = DATALENGTH(LTRIM(RTRIM([ActorId]))) AND DATALENGTH([ActorId]) BETWEEN 2 AND "
        + (AllocationDispute.MaxActorIdLength * 2);

    internal static readonly string ActorSourceCheckSql = "[ActorSource] IN (1, 2)";

    public void Configure(EntityTypeBuilder<AllocationDisputeResolution> builder)
    {
        builder.ToTable("AllocationDisputeResolutions", table =>
        {
            table.HasTrigger(TriggerName);
            table.UseSqlOutputClause(false);
            table.HasCheckConstraint(IdempotencyKeyCheckName, IdempotencyKeyCheckSql);
            table.HasCheckConstraint(OutcomeCheckName, OutcomeCheckSql);
            table.HasCheckConstraint(ReasonCheckName, ReasonCheckSql);
            table.HasCheckConstraint(ActorIdCheckName, ActorIdCheckSql);
            table.HasCheckConstraint(ActorSourceCheckName, ActorSourceCheckSql);
        });

        builder.HasKey(resolution => new
        {
            resolution.OrganizationId,
            resolution.AllocationDisputeId,
        })
            .HasName(PrimaryKeyName);

        builder.Property(resolution => resolution.OrganizationId)
            .HasConversion(ReconciliationValueConverters.OrganizationId);

        builder.Property(resolution => resolution.AllocationDisputeId)
            .HasConversion(ReconciliationValueConverters.AllocationDisputeId);

        builder.Property(resolution => resolution.Outcome)
            .HasConversion<int>();

        builder.Property(resolution => resolution.Reason)
            .HasMaxLength(AllocationDisputeResolution.MaxReasonLength)
            .IsRequired();

        builder.Property(resolution => resolution.ActorSource);

        builder.Property(resolution => resolution.ActorId)
            .HasMaxLength(AllocationDispute.MaxActorIdLength)
            .IsRequired();

        builder.Property(resolution => resolution.DecidedAt)
            .HasColumnType("datetimeoffset");

        builder.HasIndex(resolution => new
        {
            resolution.OrganizationId,
            resolution.IdempotencyKey,
        })
            .IsUnique()
            .HasDatabaseName(IdempotencyKeyIndexName);

        builder.HasOne<AllocationDispute>()
            .WithMany()
            .HasForeignKey(resolution => new
            {
                resolution.OrganizationId,
                resolution.AllocationDisputeId,
            })
            .HasPrincipalKey(dispute => new
            {
                dispute.OrganizationId,
                dispute.Id,
            })
            .HasConstraintName(ForeignKeyName)
            .OnDelete(DeleteBehavior.Restrict);
    }
}