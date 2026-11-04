using FinOpsDesk.Modules.Access.Domain;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FinOpsDesk.Modules.Access.Infrastructure.Persistence.Configurations;

internal sealed class OperationalSecurityEventConfiguration : IEntityTypeConfiguration<OperationalSecurityEvent>
{
    public void Configure(EntityTypeBuilder<OperationalSecurityEvent> builder)
    {
        builder.ToTable("OperationalSecurityEvents", table =>
        {
            table.HasCheckConstraint(
                "CK_OperationalSecurityEvents_Operation",
                "[Operation] IN (1, 2, 3)");
            table.HasCheckConstraint(
                "CK_OperationalSecurityEvents_Outcome",
                "[Outcome] IN (1, 2)");
            table.HasCheckConstraint(
                "CK_OperationalSecurityEvents_DeclaredOperator",
                "LEN(LTRIM(RTRIM([DeclaredOperator]))) > 0 AND LEN([DeclaredOperator]) <= "
                + RoleActor.OperatorNameMaxCharacters);
            table.HasCheckConstraint(
                "CK_OperationalSecurityEvents_Targets",
                """
                ([Operation] = 1 AND [TargetUserId] IS NOT NULL AND [TargetOrganizationId] IS NULL AND [TargetMembershipId] IS NULL)
                OR ([Operation] = 2 AND [TargetUserId] IS NOT NULL AND [TargetOrganizationId] IS NOT NULL AND [TargetMembershipId] IS NOT NULL)
                OR ([Operation] = 3 AND [TargetUserId] IS NULL AND [TargetOrganizationId] IS NOT NULL AND [TargetMembershipId] IS NULL)
                """);
        });

        builder.HasKey(securityEvent => securityEvent.Id);
        builder.Property(securityEvent => securityEvent.Id).ValueGeneratedNever();
        builder.Property(securityEvent => securityEvent.Operation).HasConversion<byte>().HasColumnType("tinyint");
        builder.Property(securityEvent => securityEvent.Outcome).HasConversion<byte>().HasColumnType("tinyint");
        builder.Property(securityEvent => securityEvent.DeclaredOperator)
            .HasMaxLength(RoleActor.OperatorNameMaxCharacters)
            .IsRequired();
        builder.Property(securityEvent => securityEvent.OccurredAt).HasColumnType("datetimeoffset");

        builder.HasIndex(securityEvent => new { securityEvent.TargetUserId, securityEvent.OccurredAt })
            .HasDatabaseName("IX_OperationalSecurityEvents_TargetUserId_OccurredAt");
        builder.HasIndex(securityEvent => new { securityEvent.TargetOrganizationId, securityEvent.OccurredAt })
            .HasDatabaseName("IX_OperationalSecurityEvents_TargetOrganizationId_OccurredAt");

        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(securityEvent => securityEvent.TargetUserId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Organization>()
            .WithMany()
            .HasForeignKey(securityEvent => securityEvent.TargetOrganizationId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<OrganizationMembership>()
            .WithMany()
            .HasForeignKey(securityEvent => securityEvent.TargetMembershipId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}