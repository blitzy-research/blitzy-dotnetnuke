using DnnMigration.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DnnMigration.Infrastructure.Persistence.Configurations;

/// <summary>
/// Binds the <see cref="UserRole"/> entity to the legacy <c>dbo.UserRoles</c> table.
/// </summary>
/// <remarks>
/// <para>
/// The table is the assignment of an account to a role, and it carries the two dates that make an
/// assignment time-bounded. <c>EffectiveDate</c> arrived late, at 03.02.03; <c>ExpiryDate</c> has
/// been present since the baseline. Both are nullable, and absence means unbounded in that
/// direction — which is exactly why the role service treats an absent date as open rather than
/// substituting a sentinel.
/// </para>
/// <para>
/// <c>IsTrialUsed</c> is a nullable flag, so three states are representable: the trial was used, the
/// trial was not used, and nothing is recorded. The mapping preserves all three rather than
/// collapsing the third into the second.
/// </para>
/// <para>
/// Both foreign keys cascade, so deleting an account or a role withdraws its assignments. That is
/// what allows role deletion to leave no orphaned membership behind without the service performing a
/// second sweep.
/// </para>
/// </remarks>
internal sealed class UserRoleConfiguration : IEntityTypeConfiguration<UserRole>
{
    /// <summary>
    /// Applies the mapping.
    /// </summary>
    /// <param name="builder">The builder for the role assignment entity type.</param>
    public void Configure(EntityTypeBuilder<UserRole> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("UserRoles", "dbo");

        builder.HasKey(a => a.UserRoleId).HasName("PK_UserRoles");

        builder.Property(a => a.UserRoleId)
            .HasColumnName("UserRoleID")
            .HasColumnType("int")
            .ValueGeneratedOnAdd()
            .UseIdentityColumn(1, 1);

        builder.Property(a => a.UserId)
            .HasColumnName("UserID")
            .HasColumnType("int")
            .IsRequired();

        builder.Property(a => a.RoleId)
            .HasColumnName("RoleID")
            .HasColumnType("int")
            .IsRequired();

        builder.Property(a => a.EffectiveDate)
            .HasColumnName("EffectiveDate")
            .HasColumnType("datetime");

        builder.Property(a => a.ExpiryDate)
            .HasColumnName("ExpiryDate")
            .HasColumnType("datetime");

        builder.Property(a => a.IsTrialUsed)
            .HasColumnName("IsTrialUsed")
            .HasColumnType("bit");

        builder.HasOne(a => a.User)
            .WithMany(u => u.UserRoles)
            .HasForeignKey(a => a.UserId)
            .HasConstraintName("FK_UserRoles_Users")
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(a => a.Role)
            .WithMany(r => r.UserRoles)
            .HasForeignKey(a => a.RoleId)
            .HasConstraintName("FK_UserRoles_Roles")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
