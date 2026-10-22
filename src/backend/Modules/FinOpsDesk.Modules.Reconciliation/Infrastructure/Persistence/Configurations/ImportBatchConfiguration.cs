using FinOpsDesk.Modules.Reconciliation.Domain.Imports;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Conversions;
using FinOpsDesk.SharedKernel.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Configurations;

internal sealed class ImportBatchConfiguration : IEntityTypeConfiguration<ImportBatch>
{
    public void Configure(EntityTypeBuilder<ImportBatch> builder)
    {
        builder.ToTable("ImportBatches", table =>
        {
            table.HasCheckConstraint(
                "CK_ImportBatches_TerminalStatus",
                "[Status] IN (2, 3, 4)");

            table.HasCheckConstraint(
                "CK_ImportBatches_CompletedAt",
                "[CompletedAt] >= [CreatedAt]");

            table.HasCheckConstraint(
                "CK_ImportBatches_Counters",
                "[TotalRows] >= 1 AND [ImportedRows] >= 0 AND [AlreadyExistsRows] >= 0 AND [RejectedRows] >= 0 AND [TotalRows] = ([ImportedRows] + [AlreadyExistsRows] + [RejectedRows])");

            table.HasCheckConstraint(
                "CK_ImportBatches_StatusMatchesCounters",
                "([Status] = 2 AND [RejectedRows] = 0) OR ([Status] = 3 AND ([ImportedRows] + [AlreadyExistsRows]) > 0 AND [RejectedRows] > 0) OR ([Status] = 4 AND [ImportedRows] = 0 AND [AlreadyExistsRows] = 0 AND [RejectedRows] = [TotalRows])");
        });

        builder.HasKey(batch => batch.Id);

        builder.Property(batch => batch.Id)
            .HasConversion(ReconciliationValueConverters.ImportBatchId)
            .ValueGeneratedNever();

        builder.Property(batch => batch.OrganizationId)
            .HasConversion(ReconciliationValueConverters.OrganizationId);

        builder.HasAlternateKey(batch => new
        {
            batch.OrganizationId,
            batch.Id
        });

        builder.Property(batch => batch.ImportType);

        builder.Property(batch => batch.SourceSystem)
            .HasConversion(ReconciliationValueConverters.SourceSystem)
            .HasColumnName("SourceSystem")
            .HasMaxLength(SourceSystem.MaxLength)
            .IsRequired()
            .UseCollation(SourceIdentityPropertyConfiguration.BinaryCollation);

        builder.Property(batch => batch.FileHash)
            .HasConversion(ReconciliationValueConverters.FileHash)
            .HasColumnName("FileHash")
            .HasMaxLength(FileHash.HexLength)
            .IsUnicode(false)
            .IsFixedLength()
            .IsRequired()
            .UseCollation(SourceIdentityPropertyConfiguration.BinaryCollation);

        builder.HasIndex(batch => new
        {
            batch.OrganizationId,
            batch.ImportType,
            batch.SourceSystem,
            batch.FileHash
        })
            .IsUnique()
            .HasDatabaseName(
                "UX_ImportBatches_OrganizationId_ImportType_SourceSystem_FileHash");

        builder.Property(batch => batch.FileName)
            .HasMaxLength(ImportBatch.MaxFileNameLength)
            .IsRequired();

        builder.Property(batch => batch.Status);

        builder.Property(batch => batch.TotalRows);

        builder.Property(batch => batch.ImportedRows);

        builder.Property(batch => batch.AlreadyExistsRows);

        builder.Property(batch => batch.RejectedRows);

        builder.Property(batch => batch.CreatedAt)
            .HasColumnType("datetimeoffset");

        builder.Property(batch => batch.CompletedAt)
            .HasColumnType("datetimeoffset")
            .IsRequired();

        builder.Navigation(batch => batch.RowErrors)
            .HasField("_rowErrors")
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.OwnsMany(batch => batch.RowErrors, rowError =>
        {
            rowError.ToTable("ImportRowErrors", table =>
            {
                table.HasCheckConstraint(
                    "CK_ImportRowErrors_DataRowIndex",
                    "[DataRowIndex] >= 1");

                table.HasCheckConstraint(
                    "CK_ImportRowErrors_SourceLineNumber",
                    "[SourceLineNumber] IS NULL OR [SourceLineNumber] >= 1");
            });

            rowError.Property<OrganizationId>("OrganizationId")
                .HasConversion(ReconciliationValueConverters.OrganizationId)
                .IsRequired();

            rowError.Property<ImportBatchId>("ImportBatchId")
                .HasConversion(ReconciliationValueConverters.ImportBatchId)
                .IsRequired();

            rowError.HasKey("ImportBatchId", "DataRowIndex")
                .HasName("PK_ImportRowErrors");

            rowError.WithOwner()
                .HasForeignKey("OrganizationId", "ImportBatchId")
                .HasPrincipalKey(batch => new
                {
                    batch.OrganizationId,
                    batch.Id
                })
                .HasConstraintName(
                    "FK_ImportRowErrors_ImportBatches_OrganizationId_ImportBatchId");

            // EF Core 10's OwnershipBuilder has no OnDelete method. Assigning
            // IMutableForeignKey.DeleteBehavior is kept on the live model, but the
            // snapshot generator omits OnDelete for every ownership. Loading that
            // snapshot reapplies the ownership convention, which is Cascade, so any
            // other behavior makes has-pending-model-changes fail permanently.
            // The model therefore stays Cascade. The migration foreign key is
            // hand-written as ReferentialAction.Restrict, which SQL Server stores
            // as ON DELETE NO ACTION. SaveChanges deletes row errors only when they
            // are already tracked. An untracked delete, and a direct SQL DELETE,
            // are rejected by that foreign key.

            rowError.Property(error => error.DataRowIndex)
                .ValueGeneratedNever();

            rowError.Property(error => error.SourceLineNumber);

            rowError.Property(error => error.ErrorCode)
                .HasMaxLength(200)
                .IsRequired();

            rowError.Property(error => error.Message)
                .IsRequired();

            rowError.Property(error => error.ColumnName)
                .HasMaxLength(128);
        });
    }
}