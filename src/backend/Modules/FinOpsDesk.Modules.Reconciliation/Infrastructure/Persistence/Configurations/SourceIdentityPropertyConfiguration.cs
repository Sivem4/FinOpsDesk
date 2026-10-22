using FinOpsDesk.Modules.Reconciliation.Domain.Imports;
using FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Conversions;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FinOpsDesk.Modules.Reconciliation.Infrastructure.Persistence.Configurations;

internal static class SourceIdentityPropertyConfiguration
{
    public const string BinaryCollation = "Latin1_General_100_BIN2";

    public const string NullPairConstraintSql =
        "([SourceSystem] IS NULL AND [ExternalId] IS NULL) OR ([SourceSystem] IS NOT NULL AND [ExternalId] IS NOT NULL)";

    public static void Configure(ComplexPropertyBuilder<SourceIdentity> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.IsRequired(false);

        // Nested complex properties cannot be EF constructor arguments in EF Core 10.
        // These value objects are stored as converted scalars so SourceIdentity's
        // existing constructor remains bindable.
        builder.Property(identity => identity.SourceSystem)
            .HasConversion(ReconciliationValueConverters.SourceSystem)
            .HasColumnName("SourceSystem")
            .HasMaxLength(SourceSystem.MaxLength)
            .IsRequired()
            .UseCollation(BinaryCollation);

        builder.Property(identity => identity.ExternalId)
            .HasConversion(ReconciliationValueConverters.ExternalId)
            .HasColumnName("ExternalId")
            .HasMaxLength(ExternalId.MaxLength)
            .IsRequired()
            .UseCollation(BinaryCollation);
    }
}