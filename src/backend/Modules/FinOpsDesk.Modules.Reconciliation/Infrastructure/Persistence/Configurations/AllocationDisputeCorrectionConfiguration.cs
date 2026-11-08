using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Conversions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Configurations;

internal sealed class AllocationDisputeCorrectionConfiguration : IEntityTypeConfiguration<AllocationDisputeCorrection>
{
    internal const string TriggerName = "TR_AllocationDisputeCorrections_RejectMutation";

    internal const string PrimaryKeyName = "PK_AllocationDisputeCorrections";

    internal const string IdempotencyKeyIndexName =
        "UX_AllocationDisputeCorrections_OrganizationId_IdempotencyKey";

    internal const string DisputeForeignKeyName =
        "FK_AllocationDisputeCorrections_AllocationDisputes_OrganizationId_AllocationDisputeId_AllocationId";

    internal const string AllocationForeignKeyName =
        "FK_AllocationDisputeCorrections_Allocations_OrganizationId_AllocationId";

    internal const string IdempotencyKeyCheckName = "CK_AllocationDisputeCorrections_IdempotencyKey";

    internal const string ReasonCheckName = "CK_AllocationDisputeCorrections_Reason";

    internal const string ActorIdCheckName = "CK_AllocationDisputeCorrections_ActorId";

    internal const string ActorSourceCheckName = "CK_AllocationDisputeCorrections_ActorSource";

    internal const int ImmutabilityErrorNumber = 50005;

    internal static readonly string IdempotencyKeyCheckSql =
        "[IdempotencyKey] <> '00000000-0000-0000-0000-000000000000'";

    internal static readonly string ReasonCheckSql =
        "DATALENGTH([Reason]) = DATALENGTH(LTRIM(RTRIM([Reason]))) AND DATALENGTH([Reason]) BETWEEN 2 AND "
        + (AllocationDisputeCorrection.MaxReasonLength * 2);

    internal static readonly string ActorIdCheckSql =
        "DATALENGTH([ActorId]) = DATALENGTH(LTRIM(RTRIM([ActorId]))) AND DATALENGTH([ActorId]) BETWEEN 2 AND "
        + (AllocationDispute.MaxActorIdLength * 2);

    internal static readonly string ActorSourceCheckSql = "[ActorSource] IN (1, 2)";

    public void Configure(EntityTypeBuilder<AllocationDisputeCorrection> builder)
    {
        builder.ToTable("AllocationDisputeCorrections", table =>
        {
            table.HasTrigger(TriggerName);
            table.UseSqlOutputClause(false);
            table.HasCheckConstraint(IdempotencyKeyCheckName, IdempotencyKeyCheckSql);
            table.HasCheckConstraint(ReasonCheckName, ReasonCheckSql);
            table.HasCheckConstraint(ActorIdCheckName, ActorIdCheckSql);
            table.HasCheckConstraint(ActorSourceCheckName, ActorSourceCheckSql);
        });

        builder.HasKey(correction => new
        {
            correction.OrganizationId,
            correction.AllocationDisputeId,
        })
            .HasName(PrimaryKeyName);

        builder.Property(correction => correction.OrganizationId)
            .HasConversion(ReconciliationValueConverters.OrganizationId);

        builder.Property(correction => correction.AllocationDisputeId)
            .HasConversion(ReconciliationValueConverters.AllocationDisputeId);

        builder.Property(correction => correction.AllocationId)
            .HasConversion(ReconciliationValueConverters.AllocationId);

        builder.Property(correction => correction.Reason)
            .HasMaxLength(AllocationDisputeCorrection.MaxReasonLength)
            .IsRequired();

        builder.Property(correction => correction.ActorSource);

        builder.Property(correction => correction.ActorId)
            .HasMaxLength(AllocationDispute.MaxActorIdLength)
            .IsRequired();

        builder.Property(correction => correction.CorrectedAt)
            .HasColumnType("datetimeoffset");

        builder.HasIndex(correction => new
        {
            correction.OrganizationId,
            correction.IdempotencyKey,
        })
            .IsUnique()
            .HasDatabaseName(IdempotencyKeyIndexName);

        builder.HasOne<AllocationDispute>()
            .WithMany()
            .HasForeignKey(correction => new
            {
                correction.OrganizationId,
                correction.AllocationDisputeId,
                correction.AllocationId,
            })
            .HasPrincipalKey(dispute => new
            {
                dispute.OrganizationId,
                dispute.Id,
                dispute.AllocationId,
            })
            .HasConstraintName(DisputeForeignKeyName)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Allocation>()
            .WithMany()
            .HasForeignKey(correction => new
            {
                correction.OrganizationId,
                correction.AllocationId,
            })
            .HasPrincipalKey(allocation => new
            {
                allocation.OrganizationId,
                allocation.Id,
            })
            .HasConstraintName(AllocationForeignKeyName)
            .OnDelete(DeleteBehavior.Restrict);
    }
}