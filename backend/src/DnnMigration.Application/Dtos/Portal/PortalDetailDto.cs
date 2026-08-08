using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Dtos.Portal;

/// <summary>
/// The <c>data</c> payload of the success envelope for <c>GET /api/v1/portals/{id}</c>: the
/// complete attribute set of one portal.
/// </summary>
/// <remarks>
/// <para>
/// Ported from the legacy <c>PortalInfo</c> class (<c>Library/Components/Portal/PortalInfo.vb</c>).
/// </para>
/// <para>
/// Four members cannot be written back - <see cref="Email"/>, <see cref="AdministratorRoleName"/>,
/// <see cref="RegisteredRoleName"/> and <see cref="SuperTabId"/> - because both legacy read paths
/// select through the <c>vw_Portals</c> view rather than the portal table, and the view derives
/// them (<c>Website/Providers/DataProviders/SqlDataProvider/04.05.00.SqlDataProvider</c>).
/// </para>
/// </remarks>
public sealed class PortalDetailDto
{
    // MIGRATION: the legacy class was an XML document contract - a root attribute at PortalInfo.vb, and an
    // element or ignore attribute on every property - because portal templates were exported and imported as
    // XML. None of that is reproduced: this contract is JSON and its member names are the wire names.

    // MIGRATION: two member names differ from the legacy property names, and both differences are externally
    // observable: PortalID becomes PortalId (PortalInfo.vb) and GUID becomes Guid.

    // MIGRATION: three legacy properties are deliberately absent. ProcessorCredentialReference maps the
    // legacy ProcessorPassword column and is withheld even though the view projects it, because a
    // payment-gateway credential reference must not leave the server; the two I/O-performing getters that
    // counted users and pages are Application-layer projections rather than attributes of a portal.
    //
    // ProcessorCredentialReference maps the legacy ProcessorPassword column. Omitting it is an active
    // decision rather than an oversight, because the view the read path selects through does project it
    // (04.05.00.SqlDataProvider), so it would otherwise arrive free of charge.

    // MIGRATION: where the legacy class, the legacy update entry point and the database disagree about a
    // member's type, the database wins - and "the database" means the TERMINAL column, since the upgrade
    // chain alters this table repeatedly and its baseline CREATE TABLE is not the terminal shape.

    /// <summary>Gets or sets the portal's identifier.</summary>
    /// <remarks>
    /// <c>Portals.PortalID</c> is <c>[int] IDENTITY (-1, 1) NOT NULL</c>
    /// (<c>01.00.00.SqlDataProvider</c>), so -1 is the seed and the first value the column
    /// generates, while the shipped <c>_default</c> portal row is inserted with an explicit 0 under
    /// <c>IDENTITY_INSERT</c>.
    /// </remarks>
    public int PortalId { get; set; }

    /// <summary>Gets or sets the portal's display name.</summary>
    /// <remarks>
    /// <c>Portals.PortalName</c> is <c>[nvarchar](128) NOT NULL</c>
    /// (<c>01.00.00.SqlDataProvider</c>). The member is nullable regardless, because a response
    /// contract transports whatever it is handed and asserts nothing.
    /// </remarks>
    public string? PortalName { get; set; }

    /// <summary>Gets or sets the portal's free-text description. No length or format rule applies.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the portal's search keywords as a single string.</summary>
    /// <remarks>The capital W preserves the legacy property spelling (<c>PortalInfo.vb</c>).</remarks>
    public string? KeyWords { get; set; }

    /// <summary>Gets or sets the text shown in the portal's footer.</summary>
    /// <remarks>
    /// <c>Portals.FooterText</c> is <c>[nvarchar](100) NULL</c> (<c>01.00.00.SqlDataProvider</c>) -
    /// a genuinely narrow column, not a transcription error.
    /// </remarks>
    public string? FooterText { get; set; }

    // MIGRATION: LogoFile and BackgroundFile are asymmetric between reading and writing, and round-tripping
    // them causes silent data loss.
    //
    // The stored column may hold either a relative path or a managed-file token of the form "fileid=NNN".
    // The view the read path selects through resolves the token into a path before the value ever reaches
    // this contract, with a CASE expression at 04.05.00.SqlDataProvider for the logo and for the background.

    /// <summary>
    /// Gets or sets the portal's logo, as the path the read path resolved. Not safe to echo back;
    /// see the note above.
    /// </summary>
    /// <remarks>
    /// <c>Portals.LogoFile</c> is <c>[nvarchar](50) NULL</c> (<c>01.00.00.SqlDataProvider</c>).
    /// </remarks>
    public string? LogoFile { get; set; }

    /// <summary>
    /// Gets or sets the portal's background image, as the path the read path resolved. Carries the
    /// same round-trip hazard as <see cref="LogoFile"/>; see the note above.
    /// </summary>
    public string? BackgroundFile { get; set; }

    // MIGRATION: the legacy expiry date had no absent state of its own. The property was a non-nullable
    // date, so a database null became the minimum date (Null.vb) and the legacy absence test compared only
    // the date part (PortalInfo.vb).

    /// <summary>Gets or sets the date on which the portal's hosting arrangement expires.</summary>
    /// <remarks>
    /// <c>Portals.ExpiryDate</c> is <c>[datetime] NULL</c> (<c>01.00.00.SqlDataProvider</c>); the
    /// stock <c>_default</c> portal ships with no expiry.
    /// </remarks>
    public DateTime? ExpiryDate { get; set; }

    /// <summary>Gets or sets how visitors may obtain an account on the portal.</summary>
    /// <remarks>
    /// The legacy property was a bare integer (<c>PortalInfo.vb</c>) and the column is
    /// <c>int NOT NULL DEFAULT 0</c>; the named members were recovered from the legacy
    /// administration screen's list, whose entries are None, Private, Public and Verified with
    /// values (<c>Website/admin/Portal/sitesettings.ascx</c>, bound by selected index at
    /// <c>SiteSettings.ascx.vb</c>).
    /// </remarks>
    public UserRegistrationMode UserRegistration { get; set; }

    /// <summary>
    /// Gets or sets whether the portal shows no banners, its own banners, or the host's banners.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The legacy property was a bare integer (<c>PortalInfo.vb</c>) and the column is
    /// <c>int NOT NULL DEFAULT 0</c>; the named members were recovered from the legacy screen's
    /// list, whose entries are None, Site and Host with values
    /// (<c>Website/admin/Portal/sitesettings.ascx</c>).
    /// </para>
    /// </remarks>
    public BannerAdvertisingMode BannerAdvertising { get; set; }

    /// <summary>Gets or sets the currency in which <see cref="HostFee"/> is denominated.</summary>
    /// <remarks>
    /// <c>Portals.Currency</c> is <c>[char](3) NULL</c> (<c>01.00.00.SqlDataProvider</c>) and the
    /// stock <c>_default</c> portal ships USD.
    /// </remarks>
    public string? Currency { get; set; }

    /// <summary>Gets or sets the identifier of the user who administers the portal.</summary>
    /// <remarks>
    /// <c>Portals.AdministratorId</c> is <c>[int] NULL</c> referencing the user table
    /// (<c>01.00.00.SqlDataProvider</c>).
    /// </remarks>
    public int? AdministratorId { get; set; }

    // MIGRATION: Email is not a portal attribute.
    //
    //  Two consequences are load-bearing. The member is READ-ONLY: writing it here would be writing to a user
    //  record through a portal contract.

    /// <summary>
    /// Gets or sets the administrator's email address. Read-only: it belongs to the user record,
    /// not the portal; see the note above.
    /// </summary>
    /// <remarks>
    /// Transported as plain text and deliberately neither validated nor wrapped in the domain's
    /// address type. The legacy property carried no presence, length or pattern rule
    /// (<c>PortalInfo.vb</c>), and some addresses the legacy installer itself wrote would fail an
    /// address pattern, so a response that rejected them could not report existing data.
    /// </remarks>
    public string? Email { get; set; }

    // MIGRATION: HostFee and HostSpace each carry a three-way type disagreement, and in both cases the
    // terminal column decides.
    //
    // HostFee: single-precision on the legacy class (PortalInfo.vb), double on the legacy update entry point
    // (PortalController.vb, argument ten), and money in the terminal schema (03.01.01.SqlDataProvider, with
    // its default). Mapped to a nullable decimal, because money is a scaled decimal that neither binary
    // float represents exactly.

    /// <summary>Gets or sets the recurring fee charged to the portal by its host.</summary>
    /// <remarks>
    /// The terminal column type is <c>money</c> and the amount is denominated in
    /// <see cref="Currency"/>.
    /// </remarks>
    public decimal? HostFee { get; set; }

    /// <summary>
    /// Gets or sets the disk space the portal is permitted to consume, in megabytes.
    /// </summary>
    /// <remarks>
    /// ZERO MEANS UNLIMITED, not "not recorded": the legacy administration screen instructed the
    /// operator to enter zero for unlimited space.
    /// </remarks>
    public int? HostSpace { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of pages the portal is permitted to contain.
    /// </summary>
    /// <remarks>
    /// A limit, not a measurement; the number of pages the portal actually holds is
    /// <see cref="Pages"/>.
    /// </remarks>
    public int? PageQuota { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of user accounts the portal is permitted to contain.
    /// </summary>
    /// <remarks>
    /// A limit, not a measurement; the number of accounts the portal actually holds is
    /// <see cref="Users"/>.
    /// </remarks>
    public int? UserQuota { get; set; }

    // MIGRATION: Users and Pages are measurements, not stored columns, and they are plain settable values
    // here rather than the lazily querying getters they used to be.
    //
    // Neither name appears in the view the read path selects through (04.05.00.SqlDataProvider), so neither
    // can arrive with the portal row, and the account count is a separate query in its own right
    // (Library/Providers/MembershipProviders/DataProvider/ DataProvider.vb). The legacy getters ran that
    // query on first read (PortalInfo.vb), which a property cannot do asynchronously, and blocking is not
    // permitted anywhere in this codebase.

    /// <summary>
    /// Gets or sets the number of user accounts registered against the portal. A measurement, not a
    /// limit: the permitted maximum is <see cref="UserQuota"/>.
    /// </summary>
    public int Users { get; set; }

    /// <summary>
    /// Gets or sets the number of pages defined within the portal, as the legacy <c>GetTabCount</c>
    /// counted them. A measurement, not a limit: the permitted maximum is <see cref="PageQuota"/>.
    /// </summary>
    public int Pages { get; set; }

    // MIGRATION: the two role identifiers and six page identifiers below share one hazard, recorded here
    // once rather than eight times.
    //
    // Legacy data stores an unset reference as -1, because that is the shared helper's encoding for an
    // absent whole number (Null.vb) and what its converter substituted for a database null on every read.
    // Each member is nullable here, which can express an unset reference without borrowing a value from the
    // range of real ones - but this contract does not rewrite the legacy encoding, so a record holding -1
    // arrives as -1.

    /// <summary>
    /// Gets or sets the identifier of the role whose members administer the portal.
    /// </summary>
    /// <remarks>
    /// <c>Portals.AdministratorRoleId</c> is <c>[int] NULL</c> (<c>01.00.00.SqlDataProvider</c>).
    /// </remarks>
    public int? AdministratorRoleId { get; set; }

    /// <summary>
    /// Gets or sets the administrator role's name. Read-only: derived by the read path from
    /// <see cref="AdministratorRoleId"/>, not stored against the portal.
    /// </summary>
    /// <remarks>
    /// The view resolves it with a correlated sub-select against the role table
    /// (<c>04.05.00.SqlDataProvider</c>), so it always follows the identifier, cannot be written
    /// back, and yields no value when the identifier matches no role - which is why the legacy
    /// update entry point omits it. Only the name is carried; no role contract is nested.
    /// </remarks>
    public string? AdministratorRoleName { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the role granted to every registered user of the portal.
    /// </summary>
    /// <remarks>
    /// <c>Portals.RegisteredRoleId</c> is <c>[int] NULL</c> (<c>01.00.00.SqlDataProvider</c>).
    /// </remarks>
    public int? RegisteredRoleId { get; set; }

    /// <summary>
    /// Gets or sets the registered-users role's name. Read-only: derived by the read path from
    /// <see cref="RegisteredRoleId"/>, not stored against the portal.
    /// </summary>
    /// <remarks>
    /// Resolved by the same correlated sub-select pattern as <see cref="AdministratorRoleName"/>
    /// (<c>04.05.00.SqlDataProvider</c>); it cannot be written back and yields no value when the
    /// identifier matches no role.
    /// </remarks>
    public string? RegisteredRoleName { get; set; }

    /// <summary>
    /// Gets or sets the portal's stable global identifier, generated when the portal row is
    /// created.
    /// </summary>
    /// <remarks>
    /// <c>Portals.GUID</c> is <c>[uniqueidentifier] NOT NULL</c> defaulted to <c>newid</c>
    /// (<c>01.00.00.SqlDataProvider</c>), so the member is non-nullable and no emptiness guard is
    /// applied - the generator never produces the all-zero identifier, even though the legacy
    /// helper used that value as its absent-identifier encoding
    /// (<c>Library/Components/Shared/Null.vb</c>). The legacy class excluded this property from the
    /// portal-template document, because copying an installation-scoped identifier into another
    /// installation would duplicate it; this contract is not a template and reports it plainly.
    /// </remarks>
    public Guid Guid { get; set; }

    /// <summary>
    /// Gets or sets the name of the payment gateway through which the portal takes payments.
    /// </summary>
    /// <remarks>The gateway's name only, which is why it is safe to report.</remarks>
    public string? PaymentProcessor { get; set; }

    /// <summary>Gets or sets the portal's account identifier at the payment gateway.</summary>
    /// <remarks>
    /// Text rather than a number, because a gateway account identifier is an opaque string. In the
    /// legacy update entry point this argument immediately preceded the gateway password
    /// (<c>PortalController.vb</c>); here it does not, so the two must not be assumed to travel
    /// together.
    /// </remarks>
    public string? ProcessorUserId { get; set; }

    /// <summary>Gets or sets how much site-activity history the portal retains, in DAYS.</summary>
    /// <remarks>
    /// Days, not rows: the legacy screen labelled the field "Site Log History (Days):". Nullable
    /// because a portal need not have a retention period recorded.
    /// </remarks>
    public int? SiteLogHistory { get; set; }

    /// <summary>Gets or sets the identifier of the page hosting the portal's administration menu.</summary>
    /// <remarks>Zero is a legitimate page identifier; see the note above the role identifiers.</remarks>
    public int? AdminTabId { get; set; }

    // MIGRATION: SuperTabId is not a portal attribute and is not per-portal at all.
    //
    // (SELECT TOP 1 TabID FROM ...Tabs WHERE (PortalID IS NULL) AND (ParentId IS NULL))

    /// <summary>
    /// Gets or sets the identifier of the host-level root page. Read-only, and identical for every
    /// portal; see the note above.
    /// </summary>
    public int? SuperTabId { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the page shown to a first-time visitor ahead of the portal's
    /// home page.
    /// </summary>
    /// <remarks>Nullable because a portal need not define one.</remarks>
    public int? SplashTabId { get; set; }

    /// <summary>Gets or sets the identifier of the portal's home page.</summary>
    /// <remarks>Nullable because a portal need not nominate one explicitly.</remarks>
    public int? HomeTabId { get; set; }

    /// <summary>Gets or sets the identifier of the page carrying the portal's sign-in form.</summary>
    /// <remarks>Nullable because a portal need not nominate a dedicated one.</remarks>
    public int? LoginTabId { get; set; }

    /// <summary>Gets or sets the identifier of the page carrying the portal's user-account form.</summary>
    /// <remarks>Nullable because a portal need not nominate a dedicated one.</remarks>
    public int? UserTabId { get; set; }

    /// <summary>Gets or sets the culture code the portal presents by default.</summary>
    /// <remarks>
    /// Transported as stored, neither parsed nor normalised, so an empty value may legitimately
    /// appear where a modern consumer would expect none.
    /// </remarks>
    public string? DefaultLanguage { get; set; }

    // MIGRATION: the member below keeps the legacy property's spelling, TimeZoneOffset with a capital Z
    // (PortalInfo.vb), rather than the column's, which the view projects as TimezoneOffset
    // (04.05.00.SqlDataProvider). The two never conflicted because database identifiers are matched without
    // regard to case, whereas C# and the client model are case-sensitive, so one spelling had to be chosen.

    /// <summary>
    /// Gets or sets the portal's offset from Coordinated Universal Time, in MINUTES.
    /// </summary>
    /// <remarks>
    /// Minutes rather than hours, and necessarily so, because several real zones are offset by a
    /// fraction of an hour; the legacy time-conversion helper added the stored value directly as
    /// minutes (<c>Library/Components/Users/UserTime.vb</c>).
    /// </remarks>
    public int? TimeZoneOffset { get; set; }

    /// <summary>
    /// Gets or sets the portal's home directory, relative to the application, under which its
    /// uploaded content is kept.
    /// </summary>
    /// <remarks>
    /// Portal-relative, and that is the whole of what this contract reports: the legacy class also
    /// derived an absolute server path from it, and that derived property is absent here for the
    /// reason recorded at the head of this class.
    /// </remarks>
    public string? HomeDirectory { get; set; }

    // MIGRATION: the legacy hand-written pre-generic alias dictionary keyed by lower-cased alias
    // (Library/Components/Portal/PortalAliasCollection.vb) produces no counterpart type - a read-only
    // generic list expresses the same thing natively. Read-only rather than mutable, an array or a lazy
    // sequence, so a received payload cannot be mutated in place and cannot hide deferred work behind an
    // enumeration.

    /// <summary>
    /// Gets or sets the host names through which this portal is reached, when they were loaded for this
    /// response.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No legacy counterpart; the legacy screens fetched aliases separately.
    /// </para>
    /// <para>
    /// THREE STATES, and the first two must not be collapsed.
    /// </para>
    /// </remarks>
    public IReadOnlyList<PortalAliasDto>? Aliases { get; set; }
}
