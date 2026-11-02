using FinOpsDesk.Modules.Access.Domain;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FinOpsDesk.Modules.Access.Infrastructure.Persistence.Configurations;

internal sealed class ApplicationUserConfiguration : IEntityTypeConfiguration<ApplicationUser>
{
    internal const string IdentityKeyIndexName = "UX_ApplicationUsers_IdentityKey";

    internal const string BinaryCollation = "Latin1_General_100_BIN2";

    public void Configure(EntityTypeBuilder<ApplicationUser> builder)
    {
        builder.ToTable("ApplicationUsers", table =>
        {
            table.HasCheckConstraint(
                "CK_ApplicationUsers_Status",
                "[Status] IN (1, 2)");
            table.HasCheckConstraint(
                "CK_ApplicationUsers_IssuerLength",
                "DATALENGTH([Issuer]) BETWEEN 2 AND "
                + (ApplicationUser.IssuerMaxCharacters * 2)
                + " AND DATALENGTH([Issuer]) % 2 = 0");
            table.HasCheckConstraint(
                "CK_ApplicationUsers_SubjectLength",
                "DATALENGTH([Subject]) BETWEEN 2 AND "
                + (ApplicationUser.SubjectMaxCharacters * 2)
                + " AND DATALENGTH([Subject]) % 2 = 0");
            table.HasCheckConstraint(
                "CK_ApplicationUsers_IdentityKeyLength",
                "DATALENGTH([IdentityKey]) = 32");
            table.HasCheckConstraint(
                "CK_ApplicationUsers_DisplayNameLength",
                "[DisplayName] IS NULL OR (DATALENGTH([DisplayName]) BETWEEN 2 AND "
                + (ApplicationUser.DisplayNameMaxCharacters * 2)
                + " AND DATALENGTH([DisplayName]) % 2 = 0)");
        });

        builder.HasKey(user => user.Id);
        builder.Property(user => user.Id).ValueGeneratedNever();

        builder.Property(user => user.IdentityKey)
            .HasColumnType("binary(32)")
            .HasMaxLength(32)
            .IsFixedLength()
            .IsRequired();

        builder.HasIndex(user => user.IdentityKey)
            .IsUnique()
            .HasDatabaseName(IdentityKeyIndexName);

        // BIN2 keeps stored code units stable. It is not the uniqueness rule:
        // SQL Server still equates values that differ only by trailing spaces.
        builder.Property(user => user.Issuer)
            .HasMaxLength(ApplicationUser.IssuerMaxCharacters)
            .UseCollation(BinaryCollation)
            .IsRequired();

        builder.Property(user => user.Subject)
            .HasMaxLength(ApplicationUser.SubjectMaxCharacters)
            .UseCollation(BinaryCollation)
            .IsRequired();

        builder.Property(user => user.DisplayName)
            .HasMaxLength(ApplicationUser.DisplayNameMaxCharacters);

        builder.Property(user => user.Status)
            .HasConversion<byte>()
            .HasColumnType("tinyint");

        builder.Property(user => user.SessionStamp)
            .ValueGeneratedNever();

        builder.Property(user => user.CreatedAt)
            .HasColumnType("datetimeoffset");
    }
}