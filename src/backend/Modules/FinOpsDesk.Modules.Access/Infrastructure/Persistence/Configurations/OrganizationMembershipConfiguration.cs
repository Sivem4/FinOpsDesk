using FinOpsDesk.Modules.Access.Domain;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FinOpsDesk.Modules.Access.Infrastructure.Persistence.Configurations;

internal sealed class OrganizationMembershipConfiguration : IEntityTypeConfiguration<OrganizationMembership>
{
    internal const string UserOrganizationIndexName = "UX_OrganizationMemberships_UserId_OrganizationId";

    internal const string RowVersionPropertyName = "RowVersion";

    public void Configure(EntityTypeBuilder<OrganizationMembership> builder)
    {
        builder.ToTable("OrganizationMemberships", table =>
        {
            table.HasCheckConstraint(
                "CK_OrganizationMemberships_Status",
                "[Status] IN (1, 2)");
            table.HasCheckConstraint(
                "CK_OrganizationMemberships_RevokedAt",
                "([Status] = 1 AND [RevokedAt] IS NULL) OR ([Status] = 2 AND [RevokedAt] IS NOT NULL)");
        });

        builder.HasKey(membership => membership.Id);
        builder.Property(membership => membership.Id).ValueGeneratedNever();

        builder.Property(membership => membership.Status)
            .HasConversion<byte>()
            .HasColumnType("tinyint");

        builder.Property(membership => membership.CreatedAt)
            .HasColumnType("datetimeoffset");

        builder.Property(membership => membership.RevokedAt)
            .HasColumnType("datetimeoffset");

        builder.HasIndex(membership => new
        {
            membership.UserId,
            membership.OrganizationId,
        })
            .IsUnique()
            .HasDatabaseName(UserOrganizationIndexName);

        builder.HasOne(membership => membership.User)
            .WithMany()
            .HasForeignKey(membership => membership.UserId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_OrganizationMemberships_ApplicationUsers_UserId");

        builder.HasOne(membership => membership.Organization)
            .WithMany()
            .HasForeignKey(membership => membership.OrganizationId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_OrganizationMemberships_Organizations_OrganizationId");

        builder.Property<byte[]>(RowVersionPropertyName)
            .IsRowVersion()
            .IsRequired();
    }
}