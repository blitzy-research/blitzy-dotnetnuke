using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Dtos.Portal;

/// <summary>
/// The <c>data</c> payload of the success envelope for <c>GET /api/v1/portals/{id}</c>: the complete
/// attribute set of one portal.
/// </summary>
/// <remarks>
/// Four members cannot be written back - <see cref="Email"/>, <see cref="AdministratorRoleName"/>, <see
/// cref="RegisteredRoleName"/> and <see cref="SuperTabId"/> - because both legacy read paths select through
/// the <c>vw_Portals</c> view rather than the portal table, and the view derives them
/// (<c>Website/Providers/DataProviders/SqlDataProvider/04.05.00.SqlDataProvider</c>).
/// </remarks>
public sealed class PortalDetailDto
{
    /// <summary>Gets or sets the portal's identifier.</summary>
    /// <remarks>
    /// <c>Portals.PortalID</c> is <c>[int] IDENTITY (-1, 1) NOT NULL</c> (<c>01.00.00.SqlDataProvider</c>),
    /// so -1 is the seed and the first value the column generates, while the shipped <c>_default</c> portal
    /// row is inserted with an explicit 0 under <c>IDENTITY_INSERT</c>.
    /// </remarks>
    public int PortalId { get; set; }

    /// <summary>Gets or sets the portal's display name.</summary>
    /// <remarks>
    /// <c>Portals.PortalName</c> is <c>[nvarchar](128) NOT NULL</c> (<c>01.00.00.SqlDataProvider</c>). The
    /// member is nullable regardless, because a response contract transports whatever it is handed and
    /// asserts nothing.
    /// </remarks>
    public string? PortalName { get; set; }

    /// <summary>Gets or sets the portal's free-text description. No length or format rule applies.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the portal's search keywords as a single string.</summary>
    public string? KeyWords { get; set; }

    /// <summary>Gets or sets the text shown in the portal's footer.</summary>
    /// <remarks>
    /// <c>Portals.FooterText</c> is <c>[nvarchar](100) NULL</c> (<c>01.00.00.SqlDataProvider</c>) - a
    /// genuinely narrow column, not a transcription error.
    /// </remarks>
    public string? FooterText { get; set; }

    // The stored column may hold either a relative path or a managed-file token of the form "fileid=NNN".

    /// <summary>
    /// Gets or sets the portal's logo, as the path the read path resolved. Not safe to echo back; see the
    /// note above.
    /// </summary>
    /// <remarks>
    /// <c>Portals.LogoFile</c> is <c>[nvarchar](50) NULL</c> (<c>01.00.00.SqlDataProvider</c>).
    /// </remarks>
    public string? LogoFile { get; set; }

    /// <summary>
    /// Gets or sets the portal's background image, as the path the read path resolved. Carries the same
    /// round-trip hazard as <see cref="LogoFile"/>; see the note above.
    /// </summary>
    public string? BackgroundFile { get; set; }

    /// <summary>Gets or sets the date on which the portal's hosting arrangement expires.</summary>
    public DateTime? ExpiryDate { get; set; }

    /// <summary>Gets or sets how visitors may obtain an account on the portal.</summary>
    public UserRegistrationMode UserRegistration { get; set; }

    /// <summary>Gets or sets whether the portal shows no banners, its own banners, or the host's banners.</summary>
    public BannerAdvertisingMode BannerAdvertising { get; set; }

    /// <summary>Gets or sets the currency in which <see cref="HostFee"/> is denominated.</summary>
    public string? Currency { get; set; }

    /// <summary>Gets or sets the identifier of the user who administers the portal.</summary>
    /// <remarks>
    /// <c>Portals.AdministratorId</c> is <c>[int] NULL</c> referencing the user table
    /// (<c>01.00.00.SqlDataProvider</c>).
    /// </remarks>
    public int? AdministratorId { get; set; }

    // Two consequences are load-bearing. The member is READ-ONLY: writing it here would be writing to a
    // user record through a portal contract.

    /// <summary>
    /// Gets or sets the administrator's email address. Read-only: it belongs to the user record, not the
    /// portal; see the note above.
    /// </summary>
    public string? Email { get; set; }

    // HostFee and HostSpace each carry a three-way type disagreement, and in both cases the terminal column
    // decides.

    /// <summary>Gets or sets the recurring fee charged to the portal by its host.</summary>
    /// <remarks>
    /// The terminal column type is <c>money</c> and the amount is denominated in <see cref="Currency"/>.
    /// </remarks>
    public decimal? HostFee { get; set; }

    /// <summary>Gets or sets the disk space the portal is permitted to consume, in megabytes.</summary>
    public int? HostSpace { get; set; }

    /// <summary>Gets or sets the maximum number of pages the portal is permitted to contain.</summary>
    public int? PageQuota { get; set; }

    /// <summary>Gets or sets the maximum number of user accounts the portal is permitted to contain.</summary>
    public int? UserQuota { get; set; }

    // Users and Pages are measurements rather than stored columns. Neither name appears in the view the
    // read path selects through (04.05.00.SqlDataProvider), so neither arrives with the portal row: the
    // account count is a separate query, served in the legacy stack by
    // Library/Providers/MembershipProviders/DataProvider/DataProvider.vb.

    /// <summary>
    /// Gets or sets the number of user accounts registered against the portal. A measurement, not a limit:
    /// the permitted maximum is <see cref="UserQuota"/>.
    /// </summary>
    public int Users { get; set; }

    /// <summary>
    /// Gets or sets the number of pages defined within the portal, as the legacy <c>GetTabCount</c> counted
    /// them. A measurement, not a limit: the permitted maximum is <see cref="PageQuota"/>.
    /// </summary>
    public int Pages { get; set; }

    /// <summary>Gets or sets the identifier of the role whose members administer the portal.</summary>
    public int? AdministratorRoleId { get; set; }

    /// <summary>
    /// Gets or sets the administrator role's name. Read-only: derived by the read path from <see
    /// cref="AdministratorRoleId"/>, not stored against the portal.
    /// </summary>
    /// <remarks>
    /// The view resolves it with a correlated sub-select against the role table
    /// (<c>04.05.00.SqlDataProvider</c>), so it always follows the identifier, cannot be written back, and
    /// yields no value when the identifier matches no role - which is why the legacy update entry point
    /// omits it. Only the name is carried; no role contract is nested.
    /// </remarks>
    public string? AdministratorRoleName { get; set; }

    /// <summary>Gets or sets the identifier of the role granted to every registered user of the portal.</summary>
    public int? RegisteredRoleId { get; set; }

    /// <summary>
    /// Gets or sets the registered-users role's name. Read-only: derived by the read path from <see
    /// cref="RegisteredRoleId"/>, not stored against the portal.
    /// </summary>
    public string? RegisteredRoleName { get; set; }

    /// <summary>
    /// Gets or sets the portal's stable global identifier, generated when the portal row is created.
    /// </summary>
    public Guid Guid { get; set; }

    /// <summary>Gets or sets the name of the payment gateway through which the portal takes payments.</summary>
    public string? PaymentProcessor { get; set; }

    /// <summary>Gets or sets the portal's account identifier at the payment gateway.</summary>
    public string? ProcessorUserId { get; set; }

    /// <summary>Gets or sets how much site-activity history the portal retains, in DAYS.</summary>
    public int? SiteLogHistory { get; set; }

    /// <summary>Gets or sets the identifier of the page hosting the portal's administration menu.</summary>
    public int? AdminTabId { get; set; }

    // MIGRATION: SuperTabId is not a portal attribute and is not per-portal at all.
    //
    // (SELECT TOP 1 TabID FROM ...Tabs WHERE (PortalID IS NULL) AND (ParentId IS NULL))

    /// <summary>
    /// Gets or sets the identifier of the host-level root page. Read-only, and identical for every portal;
    /// see the note above.
    /// </summary>
    public int? SuperTabId { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the page shown to a first-time visitor ahead of the portal's home
    /// page.
    /// </summary>
    public int? SplashTabId { get; set; }

    /// <summary>Gets or sets the identifier of the portal's home page.</summary>
    public int? HomeTabId { get; set; }

    /// <summary>Gets or sets the identifier of the page carrying the portal's sign-in form.</summary>
    public int? LoginTabId { get; set; }

    /// <summary>Gets or sets the identifier of the page carrying the portal's user-account form.</summary>
    public int? UserTabId { get; set; }

    /// <summary>Gets or sets the culture code the portal presents by default.</summary>
    public string? DefaultLanguage { get; set; }

    /// <summary>Gets or sets the portal's offset from Coordinated Universal Time, in MINUTES.</summary>
    public int? TimeZoneOffset { get; set; }

    /// <summary>
    /// Gets or sets the portal's home directory, relative to the application, under which its uploaded
    /// content is kept.
    /// </summary>
    public string? HomeDirectory { get; set; }

    // MIGRATION: the legacy hand-written pre-generic alias dictionary keyed by lower-cased alias produces
    // no counterpart type - a read-only generic list expresses the same thing natively.

    /// <summary>
    /// Gets or sets the host names through which this portal is reached, when they were loaded for this
    /// response.
    /// </summary>
    /// <remarks>
    /// THREE STATES, and the first two must not be collapsed.
    /// </remarks>
    public IReadOnlyList<PortalAliasDto>? Aliases { get; set; }

    /// <summary>
    /// Gets or sets the optimistic-concurrency token a caller round-trips on an update to prove it is
    /// replacing the record it read.
    /// </summary>
    /// <remarks>
    /// OPAQUE, AND DELIBERATELY SO. It is derived from the tenant's own mutable columns rather than from a
    /// version counter the schema does not have - Rule T4 forbids adding one - so it changes whenever any
    /// of them changes and reveals nothing about which. A caller stores it and sends it back; it must not
    /// be parsed, compared for ordering, or built by a client.
    /// </remarks>
    public string ConcurrencyToken { get; set; } = string.Empty;
}
