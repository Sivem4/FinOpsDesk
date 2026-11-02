using FinOpsDesk.Modules.Access.Domain;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FinOpsDesk.Modules.Access.Infrastructure.Persistence.Configurations;

internal sealed class OrganizationRoleAssignmentConfiguration : IEntityTypeConfiguration<OrganizationRoleAssignment>
{
    internal const string ActiveRoleIndexName = "UX_OrganizationRoleAssignments_MembershipId_Role_Active";

    internal const string MembershipIndexName = "IX_OrganizationRoleAssignments_MembershipId";

    internal const string RowVersionPropertyName = "RowVersion";

    public void Configure(EntityTypeBuilder<OrganizationRoleAssignment> builder)
    {
        builder.ToTable("OrganizationRoleAssignments", table =>
        {
            table.HasCheckConstraint(
                "CK_OrganizationRoleAssignments_Role",
                "[Role] IN (1, 2, 3, 4)");
            table.HasCheckConstraint(
                "CK_OrganizationRoleAssignments_GrantActor",
                """
                ([GrantActorKind] = 1 AND [GrantActorUserId] IS NOT NULL AND [GrantOperatorName] IS NULL)
                OR ([GrantActorKind] = 2 AND [GrantActorUserId] IS NULL AND [GrantOperatorName] IS NOT NULL AND LEN(LTRIM(RTRIM([GrantOperatorName]))) > 0 AND LEN([GrantOperatorName]) <= 128)
                """);
            table.HasCheckConstraint(
                "CK_OrganizationRoleAssignments_RevokeActor",
                """
                ([RevokedAt] IS NULL AND [RevokeActorKind] IS NULL AND [RevokeActorUserId] IS NULL AND [RevokeOperatorName] IS NULL)
                OR ([RevokedAt] IS NOT NULL AND [RevokedAt] > [GrantedAt] AND (([RevokeActorKind] = 1 AND [RevokeActorUserId] IS NOT NULL AND [RevokeOperatorName] IS NULL) OR ([RevokeActorKind] = 2 AND [RevokeActorUserId] IS NULL AND [RevokeOperatorName] IS NOT NULL AND LEN(LTRIM(RTRIM([RevokeOperatorName]))) > 0 AND LEN([RevokeOperatorName]) <= 128)))
                """);
        });

        builder.HasKey(assignment => assignment.Id);
        builder.Property(assignment => assignment.Id).ValueGeneratedNever();

        builder.Property(assignment => assignment.Role)
            .HasConversion<byte>()
            .HasColumnType("tinyint");

        builder.Property(assignment => assignment.GrantedAt)
            .HasColumnType("datetimeoffset");

        builder.Property(assignment => assignment.GrantActorKind)
            .HasConversion<byte>()
            .HasColumnType("tinyint");

        builder.Property(assignment => assignment.GrantOperatorName)
            .HasMaxLength(RoleActor.OperatorNameMaxCharacters);

        builder.Property(assignment => assignment.RevokedAt)
            .HasColumnType("datetimeoffset");

        builder.Property(assignment => assignment.RevokeActorKind)
            .HasConversion<byte?>()
            .HasColumnType("tinyint");

        builder.Property(assignment => assignment.RevokeOperatorName)
            .HasMaxLength(RoleActor.OperatorNameMaxCharacters);

        builder.HasIndex(assignment => new { assignment.MembershipId, assignment.Role })
            .IsUnique()
            .HasFilter("[RevokedAt] IS NULL")
            .HasDatabaseName(ActiveRoleIndexName);

        builder.HasIndex(assignment => assignment.MembershipId)
            .HasDatabaseName(MembershipIndexName);

        builder.HasOne<OrganizationMembership>()
            .WithMany()
            .HasForeignKey(assignment => assignment.MembershipId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_OrganizationRoleAssignments_OrganizationMemberships_MembershipId");

        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(assignment => assignment.GrantActorUserId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_OrganizationRoleAssignments_ApplicationUsers_GrantActorUserId");

        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(assignment => assignment.RevokeActorUserId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_OrganizationRoleAssignments_ApplicationUsers_RevokeActorUserId");

        builder.Property<byte[]>(RowVersionPropertyName)
            .IsRowVersion()
            .IsRequired();
    }
}