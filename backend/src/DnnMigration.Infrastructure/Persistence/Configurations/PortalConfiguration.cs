using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace DnnMigration.Infrastructure.Persistence.Configurations;

/// <summary>
/// Binds the <see cref="Portal"/> aggregate root to the existing, immutable DotNetNuke 4.9 table
/// <c>dbo.Portals</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every column name, store type, length, nullability and default below is taken from the <em>terminal</em>
/// state of the eighty-eight script upgrade chain under
/// <c>Website/Providers/DataProviders/SqlDataProvider/</c>, never from the baseline creation script alone.
/// </para>
/// <para>
/// The schema is read-only truth for this migration. Nothing here creates, alters or drops anything: the
/// mapping describes a table that already exists so that Entity Framework can address it, and the baseline
/// migration is deliberately empty so that no installation is ever reshaped.
/// </para>
/// </remarks>
internal sealed class PortalConfiguration : IEntityTypeConfiguration<Portal>
{
    /// <summary>Applies the mapping for the portal entity type.</summary>
    /// <param name="builder">The builder for the portal entity type.</param>
    /// <exception cref="ArgumentNullException"><c>builder</c> is <see langword="null"/>.</exception>
    public void Configure(EntityTypeBuilder<Portal> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // objectQualifier is empty and databaseOwner is "dbo" in the legacy provider registration
        // (Website/release.config:L354-L355), so the table is unprefixed and lives in dbo.
        builder.ToTable("Portals", "dbo");

        // PK_Portals PRIMARY KEY NONCLUSTERED (PortalID) - declared at 01.00.00:L471 and re-added after the
        // Tmp_Portals rebuild at 01.00.05:L1456-L1461.
        builder.HasKey(p => p.PortalId).HasName("PK_Portals").IsClustered(false);

        // PortalID is IDENTITY(-1, 1) [01.00.00:L77], so the first row a fresh install creates is
        // identified by -1 and the second by 0. -1 is simultaneously the legacy Null.NullInteger sentinel,
        // so BOTH -1 and 0 are legitimate primary-key values and neither may ever be interpreted as
        // "absent", "unset" or "not yet saved".
        builder.Property(p => p.PortalId)
            .HasColumnName("PortalID")
            .HasColumnType("int")
            .ValueGeneratedOnAdd()
            .UseIdentityColumn(-1, 1);

        builder.Property(p => p.PortalName)
            .HasColumnName("PortalName")
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(p => p.LogoFile)
            .HasColumnName("LogoFile")
            .HasMaxLength(50);

        builder.Property(p => p.FooterText)
            .HasColumnName("FooterText")
            .HasMaxLength(100);

        // Pinned to the legacy datetime type. The column predates the newer high-precision date type that
        // EF would otherwise choose by default, and the two differ in range, precision and storage, so
        // leaving this to convention would silently change the store type.
        builder.Property(p => p.ExpiryDate)
            .HasColumnName("ExpiryDate")
            .HasColumnType("datetime");

        // Both discriminators are int NOT NULL DEFAULT (0) in the terminal schema [01.00.05:L1372-L1373
        // rebuild; 03.01.01:L1116-L1117 re-typed, defaults at L1125,L1127].
        builder.Property(p => p.UserRegistration)
            .HasColumnName("UserRegistration")
            .HasColumnType("int")
            .IsRequired()
            .HasDefaultValue(UserRegistrationMode.NoRegistration);

        builder.Property(p => p.BannerAdvertising)
            .HasColumnName("BannerAdvertising")
            .HasColumnType("int")
            .IsRequired()
            .HasDefaultValue(BannerAdvertisingMode.None);

        builder.Property(p => p.AdministratorId)
            .HasColumnName("AdministratorId")
            .HasColumnType("int");

        // Fixed-length ANSI. IsFixedLength keeps the query parameter an AnsiStringFixedLength so a
        // comparison does not force a conversion on the column side.
        builder.Property(p => p.Currency)
            .HasColumnName("Currency")
            .HasColumnType("char(3)")
            .HasMaxLength(3)
            .IsFixedLength()
            .IsUnicode(false);

        builder.Property(p => p.HostFee)
            .HasColumnName("HostFee")
            .HasColumnType("money")
            .IsRequired()
            .HasDefaultValue(0m)
            .ValueGeneratedNever();

        builder.Property(p => p.HostSpace)
            .HasColumnName("HostSpace")
            .HasColumnType("int")
            .IsRequired()
            .HasDefaultValue(0);

        // Plain integer columns. They reference Roles.RoleID but carry no foreign key of their own in
        // the terminal schema, so they are scalars here and not relationships.
        builder.Property(p => p.AdministratorRoleId)
            .HasColumnName("AdministratorRoleId")
            .HasColumnType("int");

        builder.Property(p => p.RegisteredRoleId)
            .HasColumnName("RegisteredRoleId")
            .HasColumnType("int");

        builder.Property(p => p.Description)
            .HasColumnName("Description")
            .HasMaxLength(500);

        // KeyWords, with a capital W - the column really is spelled that way [01.00.02:L883].
        builder.Property(p => p.KeyWords)
            .HasColumnName("KeyWords")
            .HasMaxLength(500);

        builder.Property(p => p.BackgroundFile)
            .HasColumnName("BackgroundFile")
            .HasMaxLength(50);

        // The column is literally named GUID [01.00.00:L94].
        builder.Property(p => p.PortalGuid)
            .HasColumnName("GUID")
            .HasColumnType("uniqueidentifier")
            .IsRequired()
            .HasDefaultValueSql("newid()");

        builder.Property(p => p.PaymentProcessor)
            .HasColumnName("PaymentProcessor")
            .HasMaxLength(50);

        builder.Property(p => p.ProcessorUserId)
            .HasColumnName("ProcessorUserId")
            .HasMaxLength(50);

        // The immutable legacy column name remains ProcessorPassword, but the CLR property and all new
        // writes carry only a managed-secret reference. The 50-character width cannot safely hold envelope
        // ciphertext, so no plaintext credential or reversible encryption is stored here.
        builder.Property(p => p.ProcessorCredentialReference)
            .HasColumnName("ProcessorPassword")
            .HasMaxLength(50);

        builder.Property(p => p.SiteLogHistory)
            .HasColumnName("SiteLogHistory")
            .HasColumnType("int");

        builder.Property(p => p.HomeTabId)
            .HasColumnName("HomeTabId")
            .HasColumnType("int");

        builder.Property(p => p.LoginTabId)
            .HasColumnName("LoginTabId")
            .HasColumnType("int");

        builder.Property(p => p.UserTabId)
            .HasColumnName("UserTabId")
            .HasColumnType("int");

        // The terminal width is nvarchar(10), not the nvarchar(6) the column was first added with.
        builder.Property(p => p.DefaultLanguage)
            .HasColumnName("DefaultLanguage")
            .HasMaxLength(10)
            .IsRequired()
            .HasDefaultValue("en-US");

        // Lower-case z in the column, capital Z in the property; neither side is "corrected". The store
        // default is -8 [DF_Portals_TimezoneOffset DEFAULT ((-8)), 02.02.00:L151 and 03.01.01:L1137] but
        // the CLR default is 0, and 0 is a legitimate offset that a caller can supply.
        builder.Property(p => p.TimeZoneOffset)
            .HasColumnName("TimezoneOffset")
            .HasColumnType("int")
            .IsRequired()
            .HasDefaultValue(-8)
            .ValueGeneratedNever();

        builder.Property(p => p.AdminTabId)
            .HasColumnName("AdminTabId")
            .HasColumnType("int");

        // ANSI varchar(100), deliberately not nvarchar [02.02.02:L3822-L3823, re-typed at
        // 03.01.01:L1123, DEFAULT ('') at L1139].
        builder.Property(p => p.HomeDirectory)
            .HasColumnName("HomeDirectory")
            .HasColumnType("varchar(100)")
            .HasMaxLength(100)
            .IsUnicode(false)
            .IsRequired()
            .HasDefaultValue(string.Empty);

        builder.Property(p => p.SplashTabId)
            .HasColumnName("SplashTabId")
            .HasColumnType("int");

        // The two quotas are the last columns the chain added [04.04.00:L14-L16], both
        // int NOT NULL DEFAULT 0. Enforcing them is an Application concern, not a mapping one.
        builder.Property(p => p.PageQuota)
            .HasColumnName("PageQuota")
            .HasColumnType("int")
            .IsRequired()
            .HasDefaultValue(0);

        builder.Property(p => p.UserQuota)
            .HasColumnName("UserQuota")
            .HasColumnType("int")
            .IsRequired()
            .HasDefaultValue(0);

        // There is no PortalAlias column on this table. The baseline declared PortalAlias nvarchar(200) NOT
        // NULL [01.00.00:L78], and the lower-case statement "alter table Portals drop column portalalias"
        // removed it at 02.02.02:L3925-L3926.
    }
}
