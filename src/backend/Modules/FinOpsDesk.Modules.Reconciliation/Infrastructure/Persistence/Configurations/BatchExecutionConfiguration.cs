using FinOpsDesk.Modules.Reconciliation.Domain.BatchExecutions;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Conversions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Configurations;

internal sealed class BatchExecutionConfiguration : IEntityTypeConfiguration<BatchExecution>
{
    internal const string ShapeCheckName = "CK_BatchExecutions_Shape";

    internal const string ShapeCheckSql =
        "([Status] = 1 AND [ResultVersion] IS NULL AND [ResultPayload] IS NULL AND [CompletedAt] IS NULL) "
        + "OR ([Status] = 2 AND [ResultVersion] IS NOT NULL AND [ResultVersion] = 1 AND [ResultPayload] IS NOT NULL AND LEN([ResultPayload]) > 0 "
        + "AND [CompletedAt] IS NOT NULL AND [CompletedAt] >= [CreatedAt])";

    internal const string IdempotencyIndexName = "UX_BatchExecutions_OrganizationId_IdempotencyKey";

    public void Configure(EntityTypeBuilder<BatchExecution> builder)
    {
        builder.ToTable("BatchExecutions", table =>
        {
            table.HasCheckConstraint(ShapeCheckName, ShapeCheckSql);
        });

        builder.HasKey(execution => execution.Id);

        builder.Property(execution => execution.Id)
            .HasConversion(ReconciliationValueConverters.BatchExecutionId)
            .ValueGeneratedNever();

        builder.Property(execution => execution.OrganizationId)
            .HasConversion(ReconciliationValueConverters.OrganizationId);

        builder.Property(execution => execution.IdempotencyKey);

        builder.Property(execution => execution.Fingerprint)
            .HasConversion(ReconciliationValueConverters.BatchExecutionFingerprint)
            .HasColumnType("binary(32)")
            .IsFixedLength()
            .HasMaxLength(BatchExecutionFingerprint.Length);

        builder.Property(execution => execution.Status)
            .HasConversion(ReconciliationValueConverters.BatchExecutionStatus);

        builder.Property(execution => execution.ResultVersion);

        builder.Property(execution => execution.ResultPayload)
            .HasColumnType("nvarchar(max)");

        builder.Property(execution => execution.CreatedAt)
            .HasColumnType("datetimeoffset");

        builder.Property(execution => execution.CompletedAt)
            .HasColumnType("datetimeoffset");

        builder.HasIndex(execution => new
        {
            execution.OrganizationId,
            execution.IdempotencyKey
        })
            .IsUnique()
            .HasDatabaseName(IdempotencyIndexName);
    }
}