using FinOpsDesk.Modules.Access.Domain;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FinOpsDesk.Modules.Access.Infrastructure.Persistence.Configurations;

internal sealed class BrowserSessionConfiguration : IEntityTypeConfiguration<BrowserSession>
{
    internal const string UserIndexName = "IX_BrowserSessions_UserId";

    internal const string RowVersionPropertyName = "RowVersion";

    public void Configure(EntityTypeBuilder<BrowserSession> builder)
    {
        builder.ToTable("BrowserSessions", table =>
        {
            table.HasCheckConstraint(
                "CK_BrowserSessions_ExpiresAt",
                "[ExpiresAt] > [CreatedAt]");
        });

        builder.HasKey(session => session.Id);
        builder.Property(session => session.Id).ValueGeneratedNever();

        builder.Property(session => session.UserId);

        builder.Property(session => session.CreatedAt)
            .HasColumnType("datetimeoffset");

        builder.Property(session => session.ExpiresAt)
            .HasColumnType("datetimeoffset");

        builder.Property(session => session.RevokedAt)
            .HasColumnType("datetimeoffset");

        builder.HasIndex(session => session.UserId)
            .HasDatabaseName(UserIndexName);

        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(session => session.UserId)
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_BrowserSessions_ApplicationUsers_UserId");

        builder.Property<byte[]>(RowVersionPropertyName)
            .IsRowVersion()
            .IsRequired();
    }
}