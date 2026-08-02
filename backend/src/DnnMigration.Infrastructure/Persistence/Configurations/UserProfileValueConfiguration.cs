using DnnMigration.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DnnMigration.Infrastructure.Persistence.Configurations;

/// <summary>
/// Binds the <see cref="UserProfileValue"/> entity to the legacy <c>dbo.UserProfile</c> table.
/// </summary>
/// <remarks>
/// <para>
/// The entity is named for what it holds — one profile value — while the table is named
/// <c>UserProfile</c> in the singular, so the mapping is stated rather than inferred.
/// </para>
/// <para>
/// The table stores each value twice over: <c>PropertyValue</c> is <c>nvarchar(3750)</c> and
/// <c>PropertyText</c> is <c>ntext</c>, with the long column used when a value exceeds the short
/// one. The entity exposes a computed member that prefers the long column, and that member is
/// excluded from the mapping because it derives from the two that are mapped.
/// </para>
/// <para>
/// Both foreign keys cascade on delete, so deleting an account or a definition removes the values
/// that referenced it. The definition cascade is the reason the repository's definition-removal path
/// does not delete values itself: the database already does, and duplicating the work would either
/// be redundant or, if the two disagreed, wrong.
/// </para>
/// <para>
/// <c>LastUpdatedDate</c> is required with no store default, so every write must supply it. The user
/// service takes it from the injected clock rather than from the database, which keeps the value
/// deterministic under test.
/// </para>
/// </remarks>
internal sealed class UserProfileValueConfiguration : IEntityTypeConfiguration<UserProfileValue>
{
    /// <summary>
    /// Applies the mapping.
    /// </summary>
    /// <param name="builder">The builder for the profile value entity type.</param>
    public void Configure(EntityTypeBuilder<UserProfileValue> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("UserProfile", "dbo");

        builder.HasKey(v => v.ProfileId).HasName("PK_UserProfile");

        builder.Property(v => v.ProfileId)
            .HasColumnName("ProfileID")
            .HasColumnType("int")
            .ValueGeneratedOnAdd()
            .UseIdentityColumn(1, 1);

        builder.Property(v => v.UserId)
            .HasColumnName("UserID")
            .HasColumnType("int")
            .IsRequired();

        builder.Property(v => v.PropertyDefinitionId)
            .HasColumnName("PropertyDefinitionID")
            .HasColumnType("int")
            .IsRequired();

        builder.Property(v => v.PropertyValue)
            .HasColumnName("PropertyValue")
            .HasMaxLength(3750);

        builder.Property(v => v.PropertyText)
            .HasColumnName("PropertyText")
            .HasColumnType("ntext");

        builder.Property(v => v.Visibility)
            .HasColumnName("Visibility")
            .HasColumnType("int")
            .IsRequired()
            .HasDefaultValue(0);

        builder.Property(v => v.LastUpdatedDate)
            .HasColumnName("LastUpdatedDate")
            .HasColumnType("datetime")
            .IsRequired();

        builder.Ignore(v => v.EffectiveValue);

        builder.HasOne(v => v.User)
            .WithMany(u => u.UserProfileValues)
            .HasForeignKey(v => v.UserId)
            .HasConstraintName("FK_UserProfile_Users")
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(v => v.PropertyDefinition)
            .WithMany(d => d.UserProfileValues)
            .HasForeignKey(v => v.PropertyDefinitionId)
            .HasConstraintName("FK_UserProfile_ProfilePropertyDefinition")
            .OnDelete(DeleteBehavior.Cascade);
    }
}
