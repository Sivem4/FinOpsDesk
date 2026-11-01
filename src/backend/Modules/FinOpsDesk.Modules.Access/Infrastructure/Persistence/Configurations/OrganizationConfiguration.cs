using FinOpsDesk.Modules.Access.Domain;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FinOpsDesk.Modules.Access.Infrastructure.Persistence.Configurations;

internal sealed class OrganizationConfiguration : IEntityTypeConfiguration<Organization>
{
    internal const string RowVersionPropertyName = "RowVersion";

    public void Configure(EntityTypeBuilder<Organization> builder)
    {
        builder.ToTable("Organizations", table =>
        {
            table.HasCheckConstraint(
                "CK_Organizations_Status",
                "[Status] IN (1, 2)");
            table.HasCheckConstraint(
                "CK_Organizations_DisplayNameLength",
                "DATALENGTH([DisplayName]) BETWEEN 2 AND "
                + (Organization.DisplayNameMaxCharacters * 2)
                + " AND DATALENGTH([DisplayName]) % 2 = 0");
        });

        builder.HasKey(organization => organization.Id);
        builder.Property(organization => organization.Id).ValueGeneratedNever();

        builder.Property(organization => organization.DisplayName)
            .HasMaxLength(Organization.DisplayNameMaxCharacters)
            .IsRequired();

        builder.Property(organization => organization.Status)
            .HasConversion<byte>()
            .HasColumnType("tinyint");

        builder.Property(organization => organization.CreatedAt)
            .HasColumnType("datetimeoffset");

        builder.Property<byte[]>(RowVersionPropertyName)
            .IsRowVersion()
            .IsRequired();
    }
}