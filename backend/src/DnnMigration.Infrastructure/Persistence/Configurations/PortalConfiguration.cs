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
/// Every column name, store type, length, nullability and default below is taken from the
/// <em>terminal</em> state of the eighty-eight script upgrade chain under
/// <c>Website/Providers/DataProviders/SqlDataProvider/</c>, never from the baseline creation script
/// alone. That distinction is load-bearing: the baseline created seventeen columns, the chain then
/// added sixteen more, dropped three (<c>UploadDirectory</c>, <c>PayPalId</c> and the original
/// singular <c>portalalias</c>), rebuilt the whole table once through a <c>Tmp_Portals</c> copy, and
/// finally re-typed eight columns. A configuration derived from the first script would bind three
/// columns that no longer exist and would get four store types wrong. The arithmetic resolves to
/// exactly the thirty-one columns mapped here, and <c>04.04.00</c> is the last script to alter the
/// table at all.
/// </para>
/// <para>
/// The schema is read-only truth for this migration. Nothing here creates, alters or drops anything:
/// the mapping describes a table that already exists so that Entity Framework can address it, and
/// the baseline migration is deliberately empty so that no installation is ever reshaped.
/// </para>
/// <para>
/// Four column definitions cannot be reached by convention and are therefore pinned explicitly:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///     <c>HostFee</c> is <c>money</c>. It began life as <c>nvarchar(10)</c>, was converted by the
///     <c>Tmp_Portals</c> rebuild with an explicit <c>CONVERT(money, HostFee)</c>
///     (<c>01.00.05:L1376,L1412</c>) and was re-asserted by
///     <c>ALTER TABLE Portals ALTER COLUMN HostFee money NOT NULL</c> (<c>03.01.01:L1118</c>).
///     Treating it as text would fail on the first read of a real installation.
///     </description>
///   </item>
///   <item>
///     <description>
///     The identifier column is spelled <c>GUID</c> in full upper case, which is not an idiomatic
///     member name, so the property is <c>PortalGuid</c> and the column is named explicitly.
///     </description>
///   </item>
///   <item>
///     <description>
///     The offset column is spelled <c>TimezoneOffset</c> with a lower-case <c>z</c> while the
///     property is <c>TimeZoneOffset</c>. SQL Server resolves identifiers case-insensitively, which
///     is precisely why this difference is easy to miss and impossible to rely on.
///     </description>
///   </item>
///   <item>
///     <description>
///     <c>Currency</c> is <c>char(3)</c> and <c>HomeDirectory</c> is <c>varchar(100)</c> - both ANSI,
///     not <c>nvarchar</c>. Binding either as Unicode would force an implicit conversion on every
///     comparison and, under a case-sensitive collation, could change the result of one.
///     </description>
///   </item>
/// </list>
/// <para>
/// Store defaults are recorded for fidelity, but a default must never be allowed to overwrite a
/// value the caller actually supplied. Entity Framework omits a property from an INSERT when the
/// property still holds the CLR default and a store default is configured, letting the database
/// supply the value instead. That is harmless when the store default equals the CLR default, and
/// harmless for a non-nullable string because <c>null</c> is never a legitimate value for one. It is
/// <em>not</em> harmless for <c>TimezoneOffset</c>, whose store default is -8 while its CLR default
/// is 0 - and 0 is a perfectly ordinary offset - so that column is pinned with
/// <see cref="PropertyBuilder.ValueGeneratedNever"/> and is therefore always written. <c>HostFee</c>
/// is pinned the same way so that the mapping does not depend on the default constraint existing.
/// </para>
/// <para>
/// This type declares no constructor. <c>DnnDbContext.OnModelCreating</c> discovers configurations
/// with <c>ApplyConfigurationsFromAssembly</c>, which instantiates each candidate through its public
/// parameterless constructor. An <c>internal sealed</c> class with no declared constructor receives
/// a compiler-generated public one and is found; declaring a non-public parameterless constructor
/// would make this class silently undiscoverable, with no compile error and no model-validation
/// error, and the entity would fall back to convention mapping against a table named
/// <c>Portal</c>.
/// </para>
/// </remarks>
internal sealed class PortalConfiguration : IEntityTypeConfiguration<Portal>
{
    /// <summary>
    /// Applies the mapping for the portal entity type.
    /// </summary>
    /// <param name="builder">The builder for the portal entity type.</param>
    /// <exception cref="ArgumentNullException"><c>builder</c> is <see langword="null"/>.</exception>
    public void Configure(EntityTypeBuilder<Portal> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // objectQualifier is empty and databaseOwner is "dbo" in the legacy provider registration
        // (Website/release.config:L354-L355), so the table is unprefixed and lives in dbo.
        builder.ToTable("Portals", "dbo");

        // PK_Portals PRIMARY KEY NONCLUSTERED (PortalID) - declared at 01.00.00:L471 and re-added
        // after the Tmp_Portals rebuild at 01.00.05:L1456-L1461.
        //
        // MIGRATION: NONCLUSTERED is expressed rather than left to the provider, whose default for a
        // primary key is CLUSTERED. The model snapshot is diffed against by every future migration, so
        // a snapshot that claimed a clustered key here would be a false description of the tenant table
        // and the first scaffold to touch it would propose a key rebuild.
        builder.HasKey(p => p.PortalId).HasName("PK_Portals").IsClustered(false);

        // MIGRATION: PortalID is IDENTITY(-1, 1) [01.00.00:L77], so the first row a fresh install
        // creates is identified by -1 and the second by 0. -1 is simultaneously the legacy
        // Null.NullInteger sentinel, so BOTH -1 and 0 are legitimate primary-key values and neither
        // may ever be interpreted as "absent", "unset" or "not yet saved". The seed is preserved in
        // the model rather than defaulted to 1 so that a script generated from this model reproduces
        // the real table.
        builder.Property(p => p.PortalId)
            .HasColumnName("PortalID")
            .HasColumnType("int")
            .ValueGeneratedOnAdd()
            .UseIdentityColumn(-1, 1);

        builder.Property(p => p.PortalName)
            .HasColumnName("PortalName")
            .HasMaxLength(128)
            .IsRequired();

        // MIGRATION: nullable SQL columns map to nullable CLR properties and nothing else. The legacy
        // Null.vb sentinel table - NullInteger = -1, NullString = "", NullDate = DateTime.MinValue,
        // NullByte = 255 - is honoured here as mapping knowledge only: no value converter folds null
        // into a sentinel or a sentinel into null, in either direction. Sentinel semantics survive
        // only at the DTO and API boundary, where they are externally observable.
        builder.Property(p => p.LogoFile)
            .HasColumnName("LogoFile")
            .HasMaxLength(50);

        builder.Property(p => p.FooterText)
            .HasColumnName("FooterText")
            .HasMaxLength(100);

        // Pinned to the legacy datetime type. The column predates the newer high-precision date type
        // that EF would otherwise choose by default, and the two differ in range, precision and
        // storage, so leaving this to convention would silently change the store type.
        builder.Property(p => p.ExpiryDate)
            .HasColumnName("ExpiryDate")
            .HasColumnType("datetime");

        // Both discriminators are int NOT NULL DEFAULT (0) in the terminal schema
        // [01.00.05:L1372-L1373 rebuild; 03.01.01:L1116-L1117 re-typed, defaults at L1125,L1127].
        // They persist the plain enumeration ordinal, so no value conversion is configured: the
        // ordinals are live data that the legacy administration UI bound directly as a list index,
        // and storing the member name instead would make every existing row unreadable.
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

        // MIGRATION: the legacy property was VB Single (Library/Components/Portal/PortalInfo.vb:L42,
        // property at :L157-L163), a 32-bit binary float that cannot represent a currency amount
        // exactly. The destination is decimal bound to the schema-faithful SQL money column
        // [money NOT NULL from the Tmp_Portals rebuild at 01.00.05:L1376, re-asserted at
        // 03.01.01:L1118, DEFAULT (0) at L1129], so float rounding artefacts disappear. This is a
        // precision correction, not a change to any stored value. Never decimal(18,2), never float,
        // and never a text column with a converter: money is what the column is.
        // ValueGeneratedNever records the store default and never omits the column, so the mapping
        // does not depend on the DEFAULT constraint being present.
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

        // The column is literally named GUID [01.00.00:L94]. Its store default newid() is configured
        // because Guid.Empty is never a legitimate value for it, so letting the database generate one
        // when the property is unset is correct rather than dangerous
        // [DF_Portals_GUID DEFAULT (newid()), 01.00.05:L1404 and 03.01.01:L1133].
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

        // Stored in clear text by the legacy schema. This mapping is write-through storage and
        // nothing more; no logging, projection or diagnostic behaviour is attached to it here.
        builder.Property(p => p.ProcessorPassword)
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

        // MIGRATION: the terminal width is nvarchar(10), not the nvarchar(6) the column was first
        // added with. It was introduced at 02.02.00:L146-L147 as nvarchar(6) NOT NULL DEFAULT
        // 'en-US', re-asserted at that width by 03.01.01:L1121, and then WIDENED to nvarchar(10) by
        // ALTER TABLE Portals ALTER COLUMN DefaultLanguage nvarchar(10) NOT NULL at
        // 04.03.05:L290-L291, which no later script revisits. Mapping 6 would truncate any culture
        // code longer than six characters - for example zh-Hant-TW - on write.
        builder.Property(p => p.DefaultLanguage)
            .HasColumnName("DefaultLanguage")
            .HasMaxLength(10)
            .IsRequired()
            .HasDefaultValue("en-US");

        // Lower-case z in the column, capital Z in the property; neither side is "corrected".
        // The store default is -8 [DF_Portals_TimezoneOffset DEFAULT ((-8)), 02.02.00:L151 and
        // 03.01.01:L1137] but the CLR default is 0, and 0 is a legitimate offset that a caller can
        // supply. ValueGeneratedNever therefore records the default as metadata while guaranteeing
        // the column is always written, so an explicit 0 is stored as 0 and never rewritten to -8.
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

        // MIGRATION: there is no PortalAlias column on this table. The baseline declared
        // PortalAlias nvarchar(200) NOT NULL [01.00.00:L78], and the lower-case statement
        // "alter table Portals drop column portalalias" removed it at 02.02.02:L3925-L3926. Aliases
        // live in the separate dbo.PortalAlias table, which is why Portal exposes a PortalAliases
        // collection and no alias scalar. Two further columns are absent for the same reason and are
        // likewise not mapped: UploadDirectory, dropped at 01.00.02:L889, and PayPalId, dropped at
        // 01.00.06:L611 after its value was migrated into ProcessorUserId.
        //
        // No index is declared: the only constraint on the terminal table is PK_Portals, and the
        // chain creates no index on Portals at any version.
        //
        // No relationship is declared either. Portal holds no outbound foreign key, so every
        // relationship it participates in is configured exactly once from the DEPENDENT side, by
        // PortalAliasConfiguration, ModuleConfiguration, TabConfiguration, RoleConfiguration,
        // RoleGroupConfiguration, UserPortalConfiguration, PortalDesktopModuleConfiguration and
        // ProfilePropertyDefinitionConfiguration. Restating any of them from this principal side as
        // well would define the same relationship twice, and because ApplyConfigurationsFromAssembly
        // guarantees no ordering, the surviving delete behaviour would be whichever call ran last -
        // with no compile error and no model-validation error to reveal it. This file therefore
        // contains no relationship-building call of any kind.
        //
        // Entity<int>.Identity is not ignored explicitly: it is a get-only expression-bodied property
        // over PortalId, and EF's property-discovery convention requires a writable property, so it
        // is never a candidate for mapping in the first place.
    }
}
