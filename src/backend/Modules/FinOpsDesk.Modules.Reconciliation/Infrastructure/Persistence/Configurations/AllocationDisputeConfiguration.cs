using FinOpsDesk.Modules.Reconciliation.Domain.Allocations;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Conversions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Configurations;

internal sealed class AllocationDisputeConfiguration : IEntityTypeConfiguration<AllocationDispute>
{
    internal const string OrganizationDisputeKeyName = "AK_AllocationDisputes_OrganizationId_Id";

    internal const string OrganizationDisputeAllocationKeyName =
        "AK_AllocationDisputes_OrganizationId_Id_AllocationId";

    internal const string IdempotencyKeyIndexName = "UX_AllocationDisputes_OrganizationId_IdempotencyKey";

    internal const string IdempotencyKeyCheckName = "CK_AllocationDisputes_IdempotencyKey";

    internal const string StatementCheckName = "CK_AllocationDisputes_Statement";

    internal const string ActorIdCheckName = "CK_AllocationDisputes_ActorId";

    internal const string ActorSourceCheckName = "CK_AllocationDisputes_ActorSource";

    internal static readonly string IdempotencyKeyCheckSql =
        "[IdempotencyKey] <> '00000000-0000-0000-0000-000000000000'";

    // DATALENGTH counts trailing spaces; LEN and SQL equality do not.
    // LTRIM/RTRIM remove only U+0020. nvarchar DATALENGTH is two bytes per UTF-16 code unit.
    internal static readonly string StatementCheckSql =
        "DATALENGTH([Statement]) = DATALENGTH(LTRIM(RTRIM([Statement]))) AND DATALENGTH([Statement]) BETWEEN 2 AND "
        + (AllocationDispute.MaxStatementLength * 2);

    internal static readonly string ActorIdCheckSql =
        "DATALENGTH([ActorId]) = DATALENGTH(LTRIM(RTRIM([ActorId]))) AND DATALENGTH([ActorId]) BETWEEN 2 AND "
        + (AllocationDispute.MaxActorIdLength * 2);

    internal static readonly string ActorSourceCheckSql = "[ActorSource] IN (1, 2)";

    public void Configure(EntityTypeBuilder<AllocationDispute> builder)
    {
        builder.ToTable("AllocationDisputes", table =>
        {
            table.HasCheckConstraint(IdempotencyKeyCheckName, IdempotencyKeyCheckSql);
            table.HasCheckConstraint(StatementCheckName, StatementCheckSql);
            table.HasCheckConstraint(ActorIdCheckName, ActorIdCheckSql);
            table.HasCheckConstraint(ActorSourceCheckName, ActorSourceCheckSql);
        });

        builder.HasKey(dispute => dispute.Id);

        builder.HasAlternateKey(dispute => new
        {
            dispute.OrganizationId,
            dispute.Id,
        })
            .HasName(OrganizationDisputeKeyName);

        builder.HasAlternateKey(dispute => new
        {
            dispute.OrganizationId,
            dispute.Id,
            dispute.AllocationId,
        })
            .HasName(OrganizationDisputeAllocationKeyName);

        builder.Property(dispute => dispute.Id)
            .HasConversion(ReconciliationValueConverters.AllocationDisputeId)
            .ValueGeneratedNever();

        builder.Property(dispute => dispute.OrganizationId)
            .HasConversion(ReconciliationValueConverters.OrganizationId);

        builder.Property(dispute => dispute.AllocationId)
            .HasConversion(ReconciliationValueConverters.AllocationId);

        builder.Property(dispute => dispute.Statement)
            .HasMaxLength(AllocationDispute.MaxStatementLength)
            .IsRequired();

        builder.Property(dispute => dispute.ActorSource);

        builder.Property(dispute => dispute.ActorId)
            .HasMaxLength(AllocationDispute.MaxActorIdLength)
            .IsRequired();

        builder.Property(dispute => dispute.RecordedAt)
            .HasColumnType("datetimeoffset");

        builder.HasIndex(dispute => new
        {
            dispute.OrganizationId,
            dispute.IdempotencyKey,
        })
            .IsUnique()
            .HasDatabaseName(IdempotencyKeyIndexName);

        builder.HasOne<Allocation>()
            .WithMany()
            .HasForeignKey(dispute => new
            {
                dispute.OrganizationId,
                dispute.AllocationId,
            })
            .HasPrincipalKey(allocation => new
            {
                allocation.OrganizationId,
                allocation.Id,
            })
            .OnDelete(DeleteBehavior.Restrict);
    }
}