using DnnMigration.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DnnMigration.Infrastructure.Persistence.Configurations;

/// <summary>
/// Binds the <see cref="UserPortal"/> entity to the legacy <c>dbo.UserPortals</c> table.
/// </summary>
/// <remarks>
/// <para>
/// This table is what makes an account a member of a tenant, and it is the reason the user
/// aggregate has no deleted flag of its own: the terminal <c>dbo.Users</c> table has nine columns
/// and none of them records deletion. Removing a user from a tenant means deleting that user's row
/// here; the account row itself is removed only once no membership remains anywhere.
/// </para>
/// <para>
/// The column history matters. The baseline table had <c>Authorized</c> spelled the American way,
/// which the 02.02.01 script dropped along with <c>CreatedDate</c> and <c>LastLoginDate</c>.
/// <c>CreatedDate</c> returned at 03.00.10 with a <c>getdate()</c> default, and the flag returned at
/// 03.02.03 spelled <c>Authorised</c>. The terminal table therefore has the British spelling and no
/// last-login column at all, so binding either of the dropped names would fail at run time.
/// </para>
/// <para>
/// The storage key is not the surrogate. <c>PK_UserPortals</c> is a clustered composite key over
/// <c>UserId</c> and <c>PortalId</c>, declared in the baseline script and never replaced; the
/// <c>UserPortalId</c> identity column was added later at 02.00.00 without the primary key being
/// moved onto it. The mapping states the composite key as the schema has it and maps the identity
/// column as a store-generated non-key value, which Entity Framework reads back after an insert. The
/// domain entity continues to expose <c>UserPortalId</c> as its identity for equality purposes; the
/// two notions of identity are equivalent because the composite is unique and the identity column is
/// too.
/// </para>
/// <para>
/// The store defaults — <c>getdate()</c> for the creation timestamp and <c>1</c> for the flag — are
/// deliberately not configured. The first is provider-specific SQL that the relational test provider
/// cannot evaluate, and the application always supplies the timestamp from the injected clock. The
/// second differs from the CLR default of <see langword="false"/>, so configuring it would let a
/// request to create an unauthorised membership be silently reversed; the entity's own initialiser
/// carries that default instead.
/// </para>
/// </remarks>
internal sealed class UserPortalConfiguration : IEntityTypeConfiguration<UserPortal>
{
    /// <summary>
    /// Applies the mapping.
    /// </summary>
    /// <param name="builder">The builder for the membership entity type.</param>
    public void Configure(EntityTypeBuilder<UserPortal> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("UserPortals", "dbo");

        builder.HasKey(m => new { m.UserId, m.PortalId }).HasName("PK_UserPortals");

        builder.Property(m => m.UserId)
            .HasColumnName("UserId")
            .HasColumnType("int");

        // As with the module identifier on ModuleSettings, the sentinel is moved off zero because this
        // property is part of the primary key AND part of the foreign key to Portals, and a key-and-foreign-key
        // property holding the CLR default is read as "the principal row does not exist yet". Portals.PortalID
        // is IDENTITY(-1, 1), so the first tenant is -1 and the second is zero - a perfectly ordinary tenant
        // whose members could otherwise never be recorded.
        builder.Property(m => m.PortalId)
            .HasColumnName("PortalId")
            .HasColumnType("int")
            .HasSentinel(int.MinValue);

        builder.Property(m => m.UserPortalId)
            .HasColumnName("UserPortalId")
            .HasColumnType("int")
            .ValueGeneratedOnAdd()
            .UseIdentityColumn(1, 1);

        builder.Property(m => m.CreatedDate)
            .HasColumnName("CreatedDate")
            .HasColumnType("datetime")
            .IsRequired();

        builder.Property(m => m.Authorised)
            .HasColumnName("Authorised")
            .HasColumnType("bit")
            .IsRequired();

        builder.HasOne(m => m.User)
            .WithMany(u => u.UserPortals)
            .HasForeignKey(m => m.UserId)
            .HasConstraintName("FK_UserPortals_Users")
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(m => m.Portal)
            .WithMany(p => p.UserPortals)
            .HasForeignKey(m => m.PortalId)
            .HasConstraintName("FK_UserPortals_Portals")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
