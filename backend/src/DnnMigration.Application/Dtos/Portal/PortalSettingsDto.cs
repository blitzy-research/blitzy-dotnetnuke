using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Dtos.Portal;

/// <summary>Wire contract for one portal's configuration, as the legacy Site Settings screen presented it.</summary>
/// <remarks>
/// <para>
/// No persisted entity is exposed here in either direction, which is what allows the legacy sentinel
/// semantics below to be honoured at the API edge without contaminating the model behind it.
/// </para>
/// <para>
/// THE IDENTIFIER TRAP. <c>Portals.PortalID</c> is <c>[int] IDENTITY (-1, 1) NOT NULL</c>
/// (<c>Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider:L77</c>) and the
/// installation ships a portal whose key is zero (<c>:L7125</c>), so the identity seed is itself the legacy
/// absent-integer sentinel. Both values are real, addressable portal identifiers.
/// </para>
/// </remarks>
public sealed class PortalSettingsDto
{
    // No member of the legacy per-request PortalSettings composite appears here.

    // Email, AdministratorRoleName, RegisteredRoleName, SuperTabId Not Portals columns at all.

    // The legacy Site Settings screen also offered fields that are absent from every Portal contract here,
    // because the screen mixed portal configuration with settings belonging to excluded subsystems - a
    // search-provider selector, an inline-editing toggle, four transport-security fields, a stylesheet
    // field, a site-map field, a search-engine submission field, three control-panel option lists, four
    // skin and container pickers and a desktop-module assignment list.

    /// <summary>Gets or sets the identifier of the portal this configuration describes.</summary>
    public int PortalId { get; set; }

    /// <summary>Gets or sets the portal's display name, which the legacy screen labelled "Title:".</summary>
    public string? PortalName { get; set; }

    /// <summary>Gets or sets the portal's descriptive text.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the portal's search keywords, separated by commas.</summary>
    public string? KeyWords { get; set; }

    /// <summary>Gets or sets the footer text, which the legacy screen labelled "Copyright:".</summary>
    public string? FooterText { get; set; }

    // The terminal read path is not the Portals table but the view vw_Portals, which both read procedures
    // select from wholesale (GetPortal at 04.04.00.SqlDataProvider:L199, GetPortals at:L230). At
    // 04.05.00.SqlDataProvider:L1535-L1544 the view rewrites the logo column, resolving a
    // `fileid=`-prefixed value to the stored folder and file name and passing any other value through
    // unchanged; the background column carries a character-for-character identical rewrite at
    // L1559-L1568.

    /// <summary>Gets or sets the portal logo image reference, which the legacy screen labelled "Logo:".</summary>
    public string? LogoFile { get; set; }

    /// <summary>
    /// Gets or sets the page background image reference, which the legacy screen labelled "Body
    /// Background:".
    /// </summary>
    public string? BackgroundFile { get; set; }

    /// <summary>Gets or sets the date on which the portal's hosting contract expires.</summary>
    public DateTime? ExpiryDate { get; set; }

    /// <summary>Gets or sets the mode by which the portal admits new user accounts.</summary>
    public UserRegistrationMode UserRegistration { get; set; }

    /// <summary>Gets or sets the portal's banner-advertising mode.</summary>
    public BannerAdvertisingMode BannerAdvertising { get; set; }

    /// <summary>Gets or sets the three-letter currency code used for the portal's monetary values.</summary>
    public string? Currency { get; set; }

    /// <summary>Gets or sets the identifier of the user account that administers the portal.</summary>
    public int? AdministratorId { get; set; }

    // The hosting fee carries a genuine THREE-WAY type conflict, and the terminal schema decides it.

    /// <summary>
    /// Gets or sets the monthly monetary charge for hosting the portal, denominated in <see
    /// cref="Currency"/>.
    /// </summary>
    public decimal? HostFee { get; set; }

    // The disk-space allowance has the same THREE-WAY conflict shape as the hosting fee above - the
    // legacy class declares `Integer` (PortalInfo.vb:L165), the legacy setter `Double`
    // (PortalController.vb:L1568), and the schema `ALTER COLUMN [HostSpace] [int] NOT NULL`
    // (03.01.01.SqlDataProvider:L1119, default zero at :L1131) - but it resolves to a DIFFERENT type
    // from the fee, so the two must not be assumed to match.

    /// <summary>
    /// Gets or sets the disk-space allowance for the portal, in megabytes, where ZERO DENOTES AN UNLIMITED
    /// allowance.
    /// </summary>
    public int? HostSpace { get; set; }

    /// <summary>Gets or sets the maximum number of pages the portal may contain.</summary>
    public int? PageQuota { get; set; }

    /// <summary>Gets or sets the maximum number of user accounts the portal may contain.</summary>
    public int? UserQuota { get; set; }

    /// <summary>Gets or sets the name of the payment processor that handles the portal's payments.</summary>
    public string? PaymentProcessor { get; set; }

    /// <summary>Gets or sets the account name the portal presents to its payment processor.</summary>
    /// <remarks>
    /// Legacy <c>ProcessorUserId</c>, the <c>txtUserId</c> text box, and <c>Portals.ProcessorUserId</c>,
    /// added <c>nvarchar(50) NULL</c> at <c>01.00.06.SqlDataProvider:L600</c>. An identifier rather than a
    /// secret, so it is reported.
    /// </remarks>
    public string? ProcessorUserId { get; set; }

    /// <summary>Gets or sets the number of days of site-activity history the portal retains.</summary>
    public int? SiteLogHistory { get; set; }

    // The four page-reference members below share one sentinel contract, stated once here.

    /// <summary>Gets or sets the identifier of the portal's splash page.</summary>
    public int? SplashTabId { get; set; }

    /// <summary>Gets or sets the identifier of the portal's home page.</summary>
    public int? HomeTabId { get; set; }

    /// <summary>Gets or sets the identifier of the portal's login page.</summary>
    public int? LoginTabId { get; set; }

    /// <summary>Gets or sets the identifier of the portal's user-account page.</summary>
    public int? UserTabId { get; set; }

    /// <summary>Gets or sets the portal's default language code.</summary>
    /// <remarks>
    /// Legacy <c>DefaultLanguage</c>, the <c>cboDefaultLanguage</c> selector, and
    /// <c>Portals.DefaultLanguage</c> - added <c>nvarchar(6) NOT NULL</c> defaulting to <c>'en-US'</c> at
    /// <c>02.02.00.SqlDataProvider:L147</c> and finally widened to <c>nvarchar(10) NOT NULL</c> at
    /// <c>04.03.05.SqlDataProvider:L291</c>, which is the TERMINAL width and an illustration of why only
    /// the end state of the 88-script chain is meaningful.
    /// </remarks>
    public string? DefaultLanguage { get; set; }

    /// <summary>Gets or sets the portal's time-zone offset from coordinated universal time, in MINUTES.</summary>
    public int? TimeZoneOffset { get; set; }

    /// <summary>
    /// Gets or sets the portal's home directory, the path under which its uploaded content is kept.
    /// </summary>
    public string? HomeDirectory { get; set; }

    /// <summary>
    /// Gets the globally unique identifier of the portal, which the legacy screen displayed read-only.
    /// </summary>
    public Guid Guid { get; set; }

    /// <summary>
    /// Gets or sets the optimistic-concurrency token a caller round-trips on a settings update to prove it
    /// is replacing the record it read.
    /// </summary>
    /// <remarks>
    /// OPAQUE, AND DELIBERATELY SO. It is derived from the tenant's own mutable columns rather than from a
    /// version counter the schema does not have - Rule T4 forbids adding one - so it changes whenever any
    /// of them changes and reveals nothing about which.
    /// </remarks>
    public string ConcurrencyToken { get; set; } = string.Empty;
}
