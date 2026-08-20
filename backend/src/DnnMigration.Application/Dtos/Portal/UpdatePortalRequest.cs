using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Dtos.Portal;

/// <summary>
/// Inbound request contract for <c>PUT /api/v1/portals/{id}</c>, carrying the complete set of portal
/// attributes that the legacy site-settings screen was able to modify.
/// </summary>
/// <remarks>
/// <para>
/// The request is consumed by <c>PortalsController</c> and handed to
/// <c>PortalService.UpdatePortalAsync</c>. That method answers with <c>Result&lt;PortalDetailDto&gt;</c>,
/// which carries success, the updated portal and the reason for a failure as distinct data, and
/// <c>PortalsController</c> translates it into an HTTP status code and body.
/// </para>
/// <para>
/// The subject of the write is carried by the route. The <c>{id}</c> segment of <c>PUT
/// /api/v1/portals/{id}</c> identifies the portal being updated and is the only value the service
/// addresses; <see cref="PortalId"/> is the first of the twenty-seven members and must agree with it.
/// </para>
/// </remarks>
public sealed class UpdatePortalRequest : IPortalSettingsUpdateRequest
{
    // A second legacy overload, UpdatePortalInfo(ByVal Portal As PortalInfo) at PortalController.vb:L1524,
    // accepted the persistence entity itself and forwarded its twenty-seven members to the signature above.

    // MIGRATION: the legacy entity carried XML serialisation decoration that is dropped entirely.

    // MIGRATION: twelve of the thirty-nine public properties on PortalInfo.vb are deliberately absent from
    // this request, because the legacy update path never wrote them. Each reason was measured rather than
    // assumed:

    // A blank fee box left the local at its initialiser of zero rather than at the legacy absent-number
    // sentinel, so "no fee entered" was persisted as zero, not as a database null.

    // Six of the twenty-seven members are host-only, and the rule is enforced neither here nor by the
    // transport.

    // NEITHER -1 NOR 0 MAY BE READ AS "ABSENT" HERE. The identity seed is the same negative value the
    // legacy contract used as its absent-integer sentinel, and the stock _default portal occupies the very
    // next value, zero (01.00.00.SqlDataProvider:L7125), so both are real addressable identifiers.

    /// <summary>
    /// Gets or sets the identifier of the portal being updated, which must equal the identifier in the
    /// request path.
    /// </summary>
    /// <remarks>
    /// Measured rules: the value must equal the <c>{portalId}</c> route segment. No lower bound applies,
    /// because -1 is the first identifier the portal table issues and 0 is the shipped default portal, so
    /// both are addressable rows rather than absent markers.
    /// </remarks>
    public int PortalId { get; set; }

    /// <summary>Gets or sets the display name of the portal.</summary>
    public string? PortalName { get; set; }

    // The logo reference is asymmetric between reading and writing, and echoing a read value back into this
    // request destroys data.

    /// <summary>Gets or sets the reference to the portal logo image.</summary>
    /// <remarks>
    /// The value is either a managed-file token of the form <c>fileid=NNN</c> or a path, and the two are
    /// not interchangeable. See the migration note immediately above this property: the read and write
    /// representations differ, and echoing a read value back here breaks the file link.
    /// </remarks>
    public string? LogoFile { get; set; }

    /// <summary>Gets or sets the text rendered in the portal footer.</summary>
    /// <remarks>
    /// Legacy argument 4, <c>FooterText</c>, typed <c>String</c>, supplied by the <c>txtFooterText</c> text
    /// box.
    /// </remarks>
    public string? FooterText { get; set; }

    // A null value here is the modern expression of "no expiry". This property performs NO conversion: a
    // minimum-date instant is passed through unchanged rather than being silently reinterpreted as absent,
    // so the divergence is visible to the service instead of being absorbed by the wire contract.

    /// <summary>
    /// Gets or sets the instant at which the portal's hosting arrangement expires, or <c>null</c> when the
    /// portal does not expire.
    /// </summary>
    /// <remarks>
    /// The property is nullable because the column is nullable. See the migration note above for the
    /// minimum-date sentinel and its date-only comparison semantics.
    /// </remarks>
    public DateTime? ExpiryDate { get; set; }

    /// <summary>Gets or sets the mode by which the portal admits new user accounts.</summary>
    /// <remarks>
    /// Legacy argument 6, <c>UserRegistration</c>, typed <c>Integer</c> -- an untyped discriminator that
    /// becomes the named <see cref="UserRegistrationMode"/> here, because magic integers become named
    /// enumeration members.
    /// </remarks>
    public UserRegistrationMode UserRegistration { get; set; }

    /// <summary>
    /// Gets or sets whether the portal shows no banner advertising, serves its own, or defers to
    /// host-managed advertising.
    /// </summary>
    public BannerAdvertisingMode BannerAdvertising { get; set; }

    /// <summary>Gets or sets the ISO currency code in which <see cref="HostFee"/> is denominated.</summary>
    public string? Currency { get; set; }

    /// <summary>Gets or sets the identifier of the user who administers the portal.</summary>
    /// <remarks>
    /// The property is nullable because the column is nullable; <c>null</c> means no administrator is
    /// assigned. <c>Users.UserID</c> is declared <c>IDENTITY (1, 1)</c> at
    /// <c>01.00.00.SqlDataProvider:L98</c>, so zero never occurs as a user identifier -- but that is
    /// recorded as schema knowledge only.
    /// </remarks>
    public int? AdministratorId { get; set; }

    // ALTER TABLE {databaseOwner}{objectQualifier}Portals ALTER COLUMN [HostFee] [money] NOT NULL
    // The hosting fee has THREE conflicting source types, and the schema type wins.

    /// <summary>
    /// Gets or sets the monthly monetary hosting charge for the portal, denominated in <see
    /// cref="Currency"/>, or <c>null</c> to leave the service to apply the schema default.
    /// </summary>
    /// <remarks>
    /// The modern type is a nullable decimal. See the migration note above for the three-way type conflict,
    /// the measured terminal column type and the reason a floating-point type is declined.
    /// </remarks>
    public decimal? HostFee { get; set; }

    // ALTER TABLE {databaseOwner}{objectQualifier}Portals ALTER COLUMN [HostSpace] [int] NOT NULL
    // The disk-space quota also has three conflicting source types, and here the measured schema CORRECTS
    // the specification this file was written against.

    /// <summary>
    /// Gets or sets the disk-space quota for the portal in whole megabytes, where zero means unlimited, or
    /// <c>null</c> to leave the service to apply the schema default.
    /// </summary>
    /// <remarks>
    /// Measured rules: <b>none</b>. Uniquely among the numeric fields on this screen, <c>txtHostSpace</c>
    /// carries no validator of any kind.
    /// </remarks>
    public int? HostSpace { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of pages the portal may contain, or <c>null</c> when no page quota
    /// applies.
    /// </summary>
    public int? PageQuota { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of user accounts the portal may contain, or <c>null</c> when no user
    /// quota applies.
    /// </summary>
    /// <remarks>
    /// Measured rules: none. A blank box left the local at zero rather than at the absent-integer sentinel,
    /// so a blank field persisted a real zero quota.
    /// </remarks>
    public int? UserQuota { get; set; }

    /// <summary>Gets or sets the name of the payment processor used to bill portal subscriptions.</summary>
    public string? PaymentProcessor { get; set; }

    /// <summary>Gets or sets the account identifier presented to the payment processor.</summary>
    public string? ProcessorUserId { get; set; }

    // MIGRATION: argument 16 no longer accepts the payment-processor credential itself. The immutable
    // ProcessorPassword column is too narrow for safe envelope ciphertext, so it carries only a
    // managed-secret reference using the bounded secret:// scheme.

    /// <summary>Gets or sets the managed-secret reference for the payment processor.</summary>
    /// <remarks>
    /// Replaces legacy argument 16, <c>ProcessorPassword</c>, while preserving its position after <see
    /// cref="ProcessorUserId"/>. The value is an opaque <c>secret://</c> reference, never the referenced
    /// credential.
    /// </remarks>
    public string? ProcessorCredentialReference { get; set; }

    /// <summary>Gets or sets the descriptive summary of the portal, used as page metadata.</summary>
    /// <remarks>
    /// Legacy argument 17, <c>Description</c>, typed <c>String</c>, supplied by the <c>txtDescription</c>
    /// text box. The column was added to the portals table by a later script in the chain rather than by
    /// the baseline table, which is one of the reasons the terminal schema must be read from the whole
    /// chain and not from the baseline alone.
    /// </remarks>
    public string? Description { get; set; }

    /// <summary>Gets or sets the comma-separated keywords for the portal, used as page metadata.</summary>
    public string? KeyWords { get; set; }

    // CASE WHEN LEFT(LOWER(BackgroundFile), 6) = 'fileid' THEN (SELECT Folder + FileName FROM ...Files
    // WHERE 'fileid=' + convert(varchar...Files.FileID) = BackgroundFile) ELSE BackgroundFile END AS
    // BackgroundFile.

    /// <summary>Gets or sets the reference to the portal background image.</summary>
    /// <remarks>
    /// The value is either a managed-file token of the form <c>fileid=NNN</c> or a path. See the migration
    /// note immediately above: the read and write representations differ, and echoing a read value back
    /// here breaks the file link.
    /// </remarks>
    public string? BackgroundFile { get; set; }

    /// <summary>
    /// Gets or sets the number of days of site-activity history retained for the portal, or <c>null</c>
    /// when no retention period is set.
    /// </summary>
    /// <remarks>
    /// Legacy argument 20, <c>SiteLogHistory</c>, typed <c>Integer</c>, supplied by the
    /// <c>txtSiteLogHistory</c> text box. The resource file is explicit about the unit: the label reads
    /// "Site Log History (Days):" and the help text "The number of days of site activity that is kept for
    /// this site."
    /// </remarks>
    public int? SiteLogHistory { get; set; }

    // The four page references that follow -- arguments 21 to 24 -- all used the legacy absent-integer
    // sentinel, whose value is -1, to mean "no page assigned".

    /// <summary>
    /// Gets or sets the identifier of the page shown as the portal splash screen, or <c>null</c> when the
    /// portal has no splash page.
    /// </summary>
    public int? SplashTabId { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the portal home page, or <c>null</c> when the portal has no explicit
    /// home page.
    /// </summary>
    /// <remarks>
    /// Legacy argument 22, <c>HomeTabId</c>, typed <c>Integer</c>, supplied by the <c>cboHomeTabId</c>
    /// list. The backing column is a nullable integer, so the property is nullable.
    /// </remarks>
    public int? HomeTabId { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the portal sign-in page, or <c>null</c> when the portal has no
    /// dedicated sign-in page.
    /// </summary>
    /// <remarks>
    /// Legacy argument 23, <c>LoginTabId</c>, typed <c>Integer</c>, supplied by the <c>cboLoginTabId</c>
    /// list. The backing column is a nullable integer, so the property is nullable.
    /// </remarks>
    public int? LoginTabId { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the portal user-account page, or <c>null</c> when the portal has no
    /// dedicated user-account page.
    /// </summary>
    /// <remarks>
    /// Legacy argument 24, <c>UserTabId</c>, typed <c>Integer</c>, supplied by the <c>cboUserTabId</c>
    /// list. The backing column is a nullable integer, so the property is nullable.
    /// </remarks>
    public int? UserTabId { get; set; }

    /// <summary>Gets or sets the default culture code for the portal.</summary>
    /// <remarks>
    /// The value is carried as a plain culture code. The legacy resource-file mechanism that consumed it is
    /// not ported -- user-facing wording is authored directly in the client templates, with the legacy
    /// resource files read only as the authoritative source of that wording -- so this property records a
    /// portal preference and drives no translation runtime.
    /// </remarks>
    public string? DefaultLanguage { get; set; }

    // MIGRATION: the column name is spelled differently in the schema and in the code, and the modern C#
    // spelling is chosen deliberately.

    /// <summary>
    /// Gets or sets the portal's offset from co-ordinated universal time, in minutes, or <c>null</c> when
    /// no portal offset is set.
    /// </summary>
    public int? TimeZoneOffset { get; set; }

    /// <summary>Gets or sets the portal home directory, as a path relative to the application root.</summary>
    /// <remarks>
    /// Legacy argument 27 and the last member of the replaced signature, <c>HomeDirectory</c>, typed
    /// <c>String</c>, supplied by the <c>txtHomeDirectory</c> text box and labelled "Home Directory:" by
    /// the resource file, whose help text reads "Enter the Home Directory for this site". This argument is
    /// the fourth of those the legacy documentation header omitted.
    /// </remarks>
    public string? HomeDirectory { get; set; }

    /// <summary>
    /// Gets or sets the optimistic-concurrency token the caller read on the record it is replacing.
    /// </summary>
    /// <remarks>
    /// THE VALUE IS OPAQUE AND IS SIMPLY ROUND-TRIPPED. A caller reads it from
    /// <c>PortalDetailDto.ConcurrencyToken</c> and sends the same string back; it is compared for equality
    /// against the token derived from the record as it now stands, and a mismatch is refused with
    /// <c>portal.concurrency_conflict</c> rather than being applied. No client may construct one.
    /// </remarks>
    public string? ConcurrencyToken { get; set; }
}
