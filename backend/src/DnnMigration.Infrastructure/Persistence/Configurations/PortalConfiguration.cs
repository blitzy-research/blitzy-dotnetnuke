using System.Globalization;
using DnnMigration.Domain.Entities;
using DnnMigration.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace DnnMigration.Infrastructure.Persistence.Configurations;

/// <summary>
/// Binds the <see cref="Portal"/> aggregate root to the legacy <c>dbo.Portals</c> table.
/// </summary>
/// <remarks>
/// <para>
/// Every column name, length and nullability below is taken from the terminal state of the
/// eighty-eight script upgrade chain rather than from the baseline creation script alone. The
/// baseline created fourteen columns and the chain then added seventeen more and dropped three
/// (<c>UploadDirectory</c>, <c>PayPalId</c> and the original singular <c>portalalias</c> column),
/// so a configuration derived from the first script would bind columns that no longer exist.
/// </para>
/// <para>
/// The table's identity seed is negative: <c>PortalID int IDENTITY (-1, 1)</c>. The first row a
/// fresh installation creates therefore carries the identifier <c>-1</c> and the second carries
/// <c>0</c>. Both are genuine tenants. Because the legacy sentinel for an absent integer was also
/// <c>-1</c>, no code above this layer may treat <c>-1</c> or <c>0</c> as meaning "no portal".
/// </para>
/// <para>
/// Three column definitions diverge from what the entity's property types would suggest and are
/// handled explicitly:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///     <c>HostFee</c> is <c>nvarchar(10)</c>, not a monetary type. The entity models it as a
///     decimal because that is what it means, so a culture-invariant converter is installed.
///     Invariant formatting is essential: a server whose culture uses a comma as the decimal
///     separator would otherwise write a value that no other server could read back.
///     </description>
///   </item>
///   <item>
///     <description>
///     The offset column is spelled <c>TimezoneOffset</c> with a lower-case <c>z</c> while the
///     entity property is <c>TimeZoneOffset</c>, so the mapping cannot rely on convention.
///     </description>
///   </item>
///   <item>
///     <description>
///     <c>HomeDirectory</c> is <c>varchar(100)</c> rather than <c>nvarchar</c>, so it is declared
///     non-Unicode. Writing it as Unicode would prevent the column's index from being used and,
///     on a case-sensitive collation, could change comparison results.
///     </description>
///   </item>
/// </list>
/// <para>
/// A store default is declared here only when it cannot silently overwrite a caller's intent.
/// Entity Framework omits a column from an INSERT when the property still holds the CLR default
/// and a default is configured, letting the store supply the value; that is harmless when the
/// store default equals the CLR default, and harmless for reference types because <c>null</c> is
/// never a legitimate value for a non-nullable string. It is <em>not</em> harmless when the store
/// default differs from the CLR default of a value type, which is why <c>TimezoneOffset</c>
/// (store default <c>-8</c>) and <c>GUID</c> (store default <c>newid()</c>) are documented here
/// but deliberately not configured — the application always supplies both.
/// </para>
/// </remarks>
internal sealed class PortalConfiguration : IEntityTypeConfiguration<Portal>
{
    /// <summary>
    /// Converts the decimal host fee to and from the <c>nvarchar(10)</c> column that stores it.
    /// </summary>
    private static readonly ValueConverter<decimal, string> HostFeeConverter =
        new(fee => FormatHostFee(fee), stored => ParseHostFee(stored));

    /// <summary>
    /// Applies the mapping.
    /// </summary>
    /// <param name="builder">The builder for the portal entity type.</param>
    public void Configure(EntityTypeBuilder<Portal> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("Portals", "dbo");

        builder.HasKey(p => p.PortalId).HasName("PK_Portals");

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

        builder.Property(p => p.ExpiryDate)
            .HasColumnName("ExpiryDate")
            .HasColumnType("datetime");

        // The discriminator columns are nullable integers carrying a store default of zero, which
        // is also the CLR default of the enumerations, so configuring the default is safe.
        builder.Property(p => p.UserRegistration)
            .HasColumnName("UserRegistration")
            .HasColumnType("int")
            .HasDefaultValue(UserRegistrationMode.NoRegistration);

        builder.Property(p => p.BannerAdvertising)
            .HasColumnName("BannerAdvertising")
            .HasColumnType("int")
            .HasDefaultValue(BannerAdvertisingMode.None);

        builder.Property(p => p.AdministratorId)
            .HasColumnName("AdministratorId")
            .HasColumnType("int");

        builder.Property(p => p.Currency)
            .HasColumnName("Currency")
            .HasMaxLength(3)
            .IsFixedLength()
            .IsUnicode(false);

        builder.Property(p => p.HostFee)
            .HasColumnName("HostFee")
            .HasMaxLength(10)
            .HasConversion(HostFeeConverter);

        builder.Property(p => p.HostSpace)
            .HasColumnName("HostSpace")
            .HasColumnType("int")
            .HasDefaultValue(0);

        builder.Property(p => p.AdministratorRoleId)
            .HasColumnName("AdministratorRoleId")
            .HasColumnType("int");

        builder.Property(p => p.RegisteredRoleId)
            .HasColumnName("RegisteredRoleId")
            .HasColumnType("int");

        builder.Property(p => p.Description)
            .HasColumnName("Description")
            .HasMaxLength(500);

        builder.Property(p => p.KeyWords)
            .HasColumnName("KeyWords")
            .HasMaxLength(500);

        builder.Property(p => p.BackgroundFile)
            .HasColumnName("BackgroundFile")
            .HasMaxLength(50);

        // The column is named GUID in the schema; the property avoids that name because it would
        // shadow the framework type. The store default newid() is not configured: the application
        // always supplies a value, and encoding provider-specific SQL in the model would break the
        // relational test provider used by the integration suite.
        builder.Property(p => p.PortalGuid)
            .HasColumnName("GUID")
            .HasColumnType("uniqueidentifier")
            .IsRequired();

        builder.Property(p => p.PaymentProcessor)
            .HasColumnName("PaymentProcessor")
            .HasMaxLength(50);

        builder.Property(p => p.ProcessorUserId)
            .HasColumnName("ProcessorUserId")
            .HasMaxLength(50);

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

        builder.Property(p => p.DefaultLanguage)
            .HasColumnName("DefaultLanguage")
            .HasMaxLength(6)
            .IsRequired()
            .HasDefaultValue("en-US");

        // Lower-case z, deliberately. Store default -8; not configured because zero is a genuine
        // offset and configuring the default would silently rewrite it.
        builder.Property(p => p.TimeZoneOffset)
            .HasColumnName("TimezoneOffset")
            .HasColumnType("int")
            .IsRequired();

        builder.Property(p => p.AdminTabId)
            .HasColumnName("AdminTabId")
            .HasColumnType("int");

        builder.Property(p => p.HomeDirectory)
            .HasColumnName("HomeDirectory")
            .HasMaxLength(100)
            .IsUnicode(false)
            .IsRequired()
            .HasDefaultValue(string.Empty);

        builder.Property(p => p.SplashTabId)
            .HasColumnName("SplashTabId")
            .HasColumnType("int");

        builder.Property(p => p.PageQuota)
            .HasColumnName("PageQuota")
            .HasColumnType("int")
            .HasDefaultValue(0);

        builder.Property(p => p.UserQuota)
            .HasColumnName("UserQuota")
            .HasColumnType("int")
            .HasDefaultValue(0);

        // The four page references and the two role references are plain integer columns in the
        // schema with no foreign key of their own, so they are not modelled as relationships. The
        // collection navigations are configured from each dependent's own configuration class.
    }

    /// <summary>
    /// Formats a host fee for storage using the invariant culture.
    /// </summary>
    /// <param name="fee">The fee to format.</param>
    /// <returns>The invariant string form of <paramref name="fee"/>.</returns>
    private static string FormatHostFee(decimal fee) => fee.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Reads a stored host fee, tolerating the blank and unparseable values the legacy text column
    /// permits.
    /// </summary>
    /// <param name="stored">The stored column value.</param>
    /// <returns>The parsed fee, or zero when the stored text does not denote a number.</returns>
    /// <remarks>
    /// The legacy reader coerced an unusable value to zero rather than failing, so a row written by
    /// the original application can still be read here. Throwing instead would make one malformed
    /// row poison every query that touches the table.
    /// </remarks>
    private static decimal ParseHostFee(string stored) =>
        decimal.TryParse(stored, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal parsed)
            ? parsed
            : 0m;
}
