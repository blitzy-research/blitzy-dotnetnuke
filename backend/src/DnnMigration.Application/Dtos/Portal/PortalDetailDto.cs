using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Dtos.Portal;

/// <summary>
/// The <c>data</c> payload of the success envelope for <c>GET /api/v1/portals/{id}</c>: the complete
/// attribute set of one portal.
/// </summary>
/// <remarks>
/// <para>
/// Ported from the legacy <c>PortalInfo</c> class
/// (<c>Library/Components/Portal/PortalInfo.vb</c> lines 85 to 395). Thirty-six of its thirty-nine
/// public properties appear here; the three that do not are listed in the note below.
/// <see cref="Aliases"/> is additional and has no legacy counterpart. The type is an inert
/// carrier: no member reads a store, computes or validates, and an implementer in the application
/// layer must populate it from a portal entity rather than exposing the entity itself.
/// </para>
/// <para>
/// Four members cannot be written back - <see cref="Email"/>,
/// <see cref="AdministratorRoleName"/>, <see cref="RegisteredRoleName"/> and
/// <see cref="SuperTabId"/> - because both legacy read paths select through the <c>vw_Portals</c>
/// view rather than the portal table, and the view derives them
/// (<c>Website/Providers/DataProviders/SqlDataProvider/04.05.00.SqlDataProvider</c> lines 1530 to
/// 1587). The same view rewrites <see cref="LogoFile"/> and <see cref="BackgroundFile"/> on read,
/// which makes those two actively unsafe to echo back; see the note above them.
/// </para>
/// <para>
/// ABSENCE HAZARD - the most consequential property of this contract. Legacy data encodes "no
/// value" using values that lie inside the range of real ones: a negative whole number, an empty
/// string, the minimum date, the all-zero identifier
/// (<c>Library/Components/Shared/Null.vb</c> lines 36 to 85). Those encodings collide with real
/// data here, because the portal identity column is seeded at -1
/// (<c>01.00.00.SqlDataProvider</c> line 77) and the role and page identity columns are seeded at
/// zero (lines 115 and 140) - and the stock <c>_default</c> portal ships with administrator role
/// zero (line 7125). A consumer must therefore never infer absence from a negative number, from
/// zero or from an empty string. On this contract absence is expressed only by the absence of a
/// value.
/// </para>
/// </remarks>
public sealed class PortalDetailDto
{
    // MIGRATION: the legacy class was an XML document contract - a root attribute at
    // PortalInfo.vb line 29, and an element or ignore attribute on every property - because portal
    // templates were exported and imported as XML. None of that is reproduced: this contract is
    // JSON and its member names are the wire names.

    // MIGRATION: two member names differ from the legacy property names, and both differences are
    // externally observable: PortalID becomes PortalId (PortalInfo.vb line 85) and GUID becomes
    // Guid (line 245). Only the .NET-facing names change; the stored column names are untouched
    // and are bound by the entity configuration in the infrastructure layer.

    // MIGRATION: three of the thirty-nine legacy properties are deliberately absent, each for a
    // reason that outlives this file.
    //
    // ProcessorCredentialReference maps the legacy ProcessorPassword column. Omitting it
    // is an active decision rather than an oversight, because the view the read path selects
    // through does project it (04.05.00.SqlDataProvider line 1571), so it would otherwise arrive
    // free of charge. Its two non-secret companions, PaymentProcessor and ProcessorUserId, are
    // retained.
    //
    // HomeDirectoryMapPath (line 388) composed an absolute server filesystem path through the
    // excluded globals module. Publishing it would disclose deployment layout to any caller.
    // HomeDirectory, the portal-relative value, is retained.
    //
    // Version (line 395) is not a column of the portal table at all, so there is nothing for it to
    // report.

    // MIGRATION: where the legacy class, the legacy update entry point and the database disagree
    // about a member's type, the database wins. Note that the baseline CREATE TABLE for Portals
    // (01.00.00.SqlDataProvider lines 76 to 93) is NOT the terminal shape - the eighty-eight
    // script chain alters the table repeatedly, and the terminal column is the authority.

    /// <summary>
    /// Gets or sets the portal's identifier.
    /// </summary>
    /// <remarks>
    /// <c>Portals.PortalID</c> is <c>[int] IDENTITY (-1, 1) NOT NULL</c>
    /// (<c>01.00.00.SqlDataProvider</c> line 77), so -1 is the seed and the first value the column
    /// generates, while the shipped <c>_default</c> portal row is inserted with an explicit 0 under
    /// <c>IDENTITY_INSERT</c> (line 7125). Both are real keys. Because -1 is simultaneously the legacy
    /// encoding for an absent whole number, no value of this member may be read as "no portal".
    /// </remarks>
    public int PortalId { get; set; }

    /// <summary>
    /// Gets or sets the portal's display name.
    /// </summary>
    /// <remarks>
    /// <c>Portals.PortalName</c> is <c>[nvarchar](128) NOT NULL</c>
    /// (<c>01.00.00.SqlDataProvider</c> line 73). The member is nullable regardless, because a
    /// response contract transports whatever it is handed and asserts nothing.
    /// </remarks>
    public string? PortalName { get; set; }

    /// <summary>
    /// Gets or sets the portal's free-text description. No length or format rule applies.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the portal's search keywords as a single string.
    /// </summary>
    /// <remarks>
    /// The capital W preserves the legacy property spelling (<c>PortalInfo.vb</c> line 229). The
    /// legacy screen expected a comma-separated list; it is transported unsplit and unparsed.
    /// </remarks>
    public string? KeyWords { get; set; }

    /// <summary>
    /// Gets or sets the text shown in the portal's footer.
    /// </summary>
    /// <remarks>
    /// <c>Portals.FooterText</c> is <c>[nvarchar](100) NULL</c>
    /// (<c>01.00.00.SqlDataProvider</c> line 75) - a genuinely narrow column, not a transcription
    /// error.
    /// </remarks>
    public string? FooterText { get; set; }

    // MIGRATION: LogoFile and BackgroundFile are asymmetric between reading and writing, and
    // round-tripping them causes silent data loss.
    //
    // The stored column may hold either a relative path or a managed-file token of the form
    // "fileid=NNN". The view the read path selects through resolves the token into a path before
    // the value ever reaches this contract, with a CASE expression at 04.05.00.SqlDataProvider
    // lines 1535 to 1544 for the logo and 1559 to 1568 for the background. What arrives here is
    // therefore the RESOLVED path, never the token.
    //
    // Writing that resolved path back replaces the token with a literal path and permanently
    // severs the link to the managed file. A caller that means "leave this unchanged" must OMIT
    // the field on update rather than echoing the value it read.

    /// <summary>
    /// Gets or sets the portal's logo, as the path the read path resolved. Not safe to echo back;
    /// see the note above.
    /// </summary>
    /// <remarks>
    /// <c>Portals.LogoFile</c> is <c>[nvarchar](50) NULL</c>
    /// (<c>01.00.00.SqlDataProvider</c> line 74).
    /// </remarks>
    public string? LogoFile { get; set; }

    /// <summary>
    /// Gets or sets the portal's background image, as the path the read path resolved. Carries the
    /// same round-trip hazard as <see cref="LogoFile"/>; see the note above.
    /// </summary>
    public string? BackgroundFile { get; set; }

    // MIGRATION: the legacy expiry date had no absent state of its own. The property was a
    // non-nullable date, so a database null became the minimum date (Null.vb lines 66 to 70 and
    // 88) and the legacy absence test compared only the date part (PortalInfo.vb lines 222 to
    // 223). The member below is nullable, which is the honest shape, but the legacy encoding is
    // transported rather than rewritten: a record holding the minimum date arrives as that date.

    /// <summary>
    /// Gets or sets the date on which the portal's hosting arrangement expires.
    /// </summary>
    /// <remarks>
    /// <c>Portals.ExpiryDate</c> is <c>[datetime] NULL</c>
    /// (<c>01.00.00.SqlDataProvider</c> line 76); the stock <c>_default</c> portal ships with no
    /// expiry (line 7125). See the note above for the legacy absent-value encoding.
    /// </remarks>
    public DateTime? ExpiryDate { get; set; }

    /// <summary>
    /// Gets or sets how visitors may obtain an account on the portal.
    /// </summary>
    /// <remarks>
    /// The legacy property was a bare integer (<c>PortalInfo.vb</c> line 125) and the column is
    /// <c>int NOT NULL DEFAULT 0</c>; the named members were recovered from the legacy
    /// administration screen's list, whose entries are None, Private, Public and Verified with
    /// values 0 to 3 (<c>Website/admin/Portal/sitesettings.ascx</c> lines 230 to 233, bound by
    /// selected index at <c>SiteSettings.ascx.vb</c> line 277). The stock <c>_default</c> portal
    /// ships as Public (line 7125). Serialised as its numeric value, so the numbers are the
    /// contract.
    /// </remarks>
    public UserRegistrationMode UserRegistration { get; set; }

    /// <summary>
    /// Gets or sets whether the portal shows no banners, its own banners, or the host's banners.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The legacy property was a bare integer (<c>PortalInfo.vb</c> line 133) and the column is
    /// <c>int NOT NULL DEFAULT 0</c>; the named members were recovered from the legacy screen's
    /// list, whose entries are None, Site and Host with values 0 to 2
    /// (<c>Website/admin/Portal/sitesettings.ascx</c> lines 123 to 125). The stock <c>_default</c>
    /// portal ships with banners off (line 7125). Serialised as its numeric value.
    /// </para>
    /// <para>
    /// One live rule attaches to the Host member: the legacy screen disabled portal-level banner
    /// editing and revealed an explanatory label whenever the mode was Host, for every caller
    /// except a super-user (<c>SiteSettings.ascx.vb</c> lines 295 and 296). An implementer of the
    /// portal service, and of the client's settings form, is obliged to reproduce that rule; this
    /// contract only reports the value.
    /// </para>
    /// </remarks>
    public BannerAdvertisingMode BannerAdvertising { get; set; }

    /// <summary>
    /// Gets or sets the currency in which <see cref="HostFee"/> is denominated.
    /// </summary>
    /// <remarks>
    /// <c>Portals.Currency</c> is <c>[char](3) NULL</c> (<c>01.00.00.SqlDataProvider</c> line 82)
    /// and the stock <c>_default</c> portal ships USD (line 7125). Because the column is
    /// fixed-width, stored values may arrive space-padded; this contract does not trim them.
    /// </remarks>
    public string? Currency { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the user who administers the portal.
    /// </summary>
    /// <remarks>
    /// <c>Portals.AdministratorId</c> is <c>[int] NULL</c> referencing the user table
    /// (<c>01.00.00.SqlDataProvider</c> line 80). This is the join key from which
    /// <see cref="Email"/> is derived, so the two are only ever consistent with each other.
    /// </remarks>
    public int? AdministratorId { get; set; }

    // MIGRATION: Email is not a portal attribute. No version of the Portals table in the
    // eighty-eight script chain has an email column; the value reaches this contract only because
    // the view the read path selects through joins the administrator's user row -
    // LEFT OUTER JOIN Users AS U ON P.AdministratorId = U.UserID (04.05.00.SqlDataProvider lines
    // 1586 to 1587) - and projects Users.Email unqualified at line 1574.
    //
    // Two consequences are load-bearing. The member is READ-ONLY: writing it here would be writing
    // to a user record through a portal contract. And the join is an OUTER one, so a portal whose
    // AdministratorId matches no user yields no value at all, which is why the member must be
    // nullable.
    //
    // The backing column's TERMINAL shape is nvarchar(256) NULL. The baseline declared it
    // nvarchar(100) NOT NULL, a later script dropped the column outright, and 03.00.13
    // .SqlDataProvider lines 109 to 110 re-added it at the wider nullable shape, which the
    // terminal procedure parameters agree with. Anything sizing a buffer or a validator from this
    // member must use 256, not the baseline width.

    /// <summary>
    /// Gets or sets the administrator's email address. Read-only: it belongs to the user record,
    /// not the portal; see the note above.
    /// </summary>
    /// <remarks>
    /// Transported as plain text and deliberately neither validated nor wrapped in the domain's
    /// address type. The legacy property carried no presence, length or pattern rule
    /// (<c>PortalInfo.vb</c> line 285), and some addresses the legacy installer itself wrote would
    /// fail an address pattern, so a response that rejected them could not report existing data.
    /// </remarks>
    public string? Email { get; set; }

    // MIGRATION: HostFee and HostSpace each carry a three-way type disagreement, and in both cases
    // the terminal column decides.
    //
    // HostFee: single-precision on the legacy class (PortalInfo.vb line 157), double on the legacy
    // update entry point (PortalController.vb line 1568, argument ten), and money in the terminal
    // schema (03.01.01.SqlDataProvider line 1118, with its default at line 1129). Mapped to a
    // nullable decimal, because money is a scaled decimal that neither binary float represents
    // exactly.
    //
    // HostSpace: integer on the legacy class (line 165), double on the update entry point
    // (argument eleven), and int in the terminal schema (03.01.01.SqlDataProvider line 1119, with
    // its default at line 1131). Mapped to a nullable whole number.
    //
    // Neither member is clamped. The zero clamps in the legacy portal controller (lines 395 and
    // 398) guard a ROLE's service and trial fees, not the portal hosting fee.

    /// <summary>
    /// Gets or sets the recurring fee charged to the portal by its host.
    /// </summary>
    /// <remarks>
    /// The terminal column type is <c>money</c> and the amount is denominated in
    /// <see cref="Currency"/>. See the note above for why this is a decimal.
    /// </remarks>
    public decimal? HostFee { get; set; }

    /// <summary>
    /// Gets or sets the disk space the portal is permitted to consume, in megabytes.
    /// </summary>
    /// <remarks>
    /// ZERO MEANS UNLIMITED, not "not recorded": the legacy administration screen instructed the
    /// operator to enter zero for unlimited space. The stock <c>_default</c> portal ships a small
    /// finite quota (<c>01.00.00.SqlDataProvider</c> line 7125).
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

    // MIGRATION: Users and Pages are measurements, not stored columns, and they are plain settable
    // values here rather than the lazily querying getters they used to be.
    //
    // Neither name appears in the view the read path selects through (04.05.00.SqlDataProvider
    // lines 1530 to 1587), so neither can arrive with the portal row, and the account count is a
    // separate query in its own right (Library/Providers/MembershipProviders/DataProvider/
    // DataProvider.vb line 82). The legacy getters ran that query on first read (PortalInfo.vb
    // lines 311 to 313 and 322 to 325), which a property cannot do asynchronously, and blocking is
    // not permitted anywhere in this codebase. An implementer of the portal service is therefore
    // obliged to assign both from counts it has already awaited. Both are non-nullable and start
    // at zero.
    //
    // MIGRATION: the PAGE tally can legitimately be NEGATIVE on the wire, and that is preserved legacy
    // arithmetic rather than a defect. The legacy getter resolved it through GetTabCount, whose terminal
    // definition (04.04.00.SqlDataProvider lines 511 to 527) is SELECT COUNT(*) - 1 over the portal's
    // pages with the administration page and its direct children excluded; a portal that records no
    // administration page made every row's predicate unknown, so the expression evaluated to 0 - 1 and
    // the grid displayed minus one. The value is carried through exactly as the counting query produces
    // it, because clamping it to zero here would report a figure the legacy application never showed and
    // would hide the misconfiguration the negative value announces. It is NOT the legacy Null.NullInteger
    // sentinel and a consumer must not read it as "unknown".

    /// <summary>
    /// Gets or sets the number of user accounts registered against the portal. A measurement, not
    /// a limit: the permitted maximum is <see cref="UserQuota"/>. See the note above for how it is
    /// populated.
    /// </summary>
    public int Users { get; set; }

    /// <summary>
    /// Gets or sets the number of pages defined within the portal, as the legacy
    /// <c>GetTabCount</c> counted them. A measurement, not a limit: the permitted maximum is
    /// <see cref="PageQuota"/>. See the note above for how it is populated, for the three
    /// counter-intuitive parts of the legacy predicate, and for why the value may be minus one.
    /// </summary>
    public int Pages { get; set; }

    // MIGRATION: the two role identifiers and six page identifiers below share one hazard,
    // recorded here once rather than eight times.
    //
    // Legacy data stores an unset reference as -1, because that is the shared helper's encoding
    // for an absent whole number (Null.vb lines 41 to 45) and what its converter substituted for a
    // database null on every read (line 88). Each member is nullable here, which can express an
    // unset reference without borrowing a value from the range of real ones - but this contract
    // does not rewrite the legacy encoding, so a record holding -1 arrives as -1. Whether a mapper
    // forwards that number or omits the value instead is a decision an implementer must make
    // explicitly.
    //
    // The inference does not run backwards. -1 does not prove a reference is unset, and neither
    // does zero: the role and page identity columns are both seeded at zero
    // (01.00.00.SqlDataProvider lines 115 and 140), and the stock _default portal's administrator
    // role identifier IS zero (line 7125). The only sound test is the absence of a value.

    /// <summary>
    /// Gets or sets the identifier of the role whose members administer the portal.
    /// </summary>
    /// <remarks>
    /// <c>Portals.AdministratorRoleId</c> is <c>[int] NULL</c>
    /// (<c>01.00.00.SqlDataProvider</c> line 83). Zero is legitimate and the stock <c>_default</c>
    /// portal uses it; see the note above.
    /// </remarks>
    public int? AdministratorRoleId { get; set; }

    /// <summary>
    /// Gets or sets the administrator role's name. Read-only: derived by the read path from
    /// <see cref="AdministratorRoleId"/>, not stored against the portal.
    /// </summary>
    /// <remarks>
    /// The view resolves it with a correlated sub-select against the role table
    /// (<c>04.05.00.SqlDataProvider</c> line 1584), so it always follows the identifier, cannot be
    /// written back, and yields no value when the identifier matches no role - which is why the
    /// legacy update entry point omits it. Only the name is carried; no role contract is nested.
    /// </remarks>
    public string? AdministratorRoleName { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the role granted to every registered user of the portal.
    /// </summary>
    /// <remarks>
    /// <c>Portals.RegisteredRoleId</c> is <c>[int] NULL</c>
    /// (<c>01.00.00.SqlDataProvider</c> line 84). Zero is legitimate; see the note above.
    /// </remarks>
    public int? RegisteredRoleId { get; set; }

    /// <summary>
    /// Gets or sets the registered-users role's name. Read-only: derived by the read path from
    /// <see cref="RegisteredRoleId"/>, not stored against the portal.
    /// </summary>
    /// <remarks>
    /// Resolved by the same correlated sub-select pattern as <see cref="AdministratorRoleName"/>
    /// (<c>04.05.00.SqlDataProvider</c> line 1585); it cannot be written back and yields no value
    /// when the identifier matches no role.
    /// </remarks>
    public string? RegisteredRoleName { get; set; }

    /// <summary>
    /// Gets or sets the portal's stable global identifier, generated when the portal row is
    /// created.
    /// </summary>
    /// <remarks>
    /// <c>Portals.GUID</c> is <c>[uniqueidentifier] NOT NULL</c> defaulted to <c>newid()</c>
    /// (<c>01.00.00.SqlDataProvider</c> line 91), so the member is non-nullable and no emptiness
    /// guard is applied - the generator never produces the all-zero identifier, even though the
    /// legacy helper used that value as its absent-identifier encoding
    /// (<c>Library/Components/Shared/Null.vb</c> lines 81 to 85). The legacy class excluded this
    /// property from the portal-template document, because copying an installation-scoped
    /// identifier into another installation would duplicate it; this contract is not a template
    /// and reports it plainly.
    /// </remarks>
    public Guid Guid { get; set; }

    /// <summary>
    /// Gets or sets the name of the payment gateway through which the portal takes payments.
    /// </summary>
    /// <remarks>
    /// The gateway's name only, which is why it is safe to report. Its account identifier is
    /// <see cref="ProcessorUserId"/>; the third part of the legacy gateway configuration, the
    /// password, is absent for the reason recorded at the head of this class.
    /// </remarks>
    public string? PaymentProcessor { get; set; }

    /// <summary>
    /// Gets or sets the portal's account identifier at the payment gateway.
    /// </summary>
    /// <remarks>
    /// Text rather than a number, because a gateway account identifier is an opaque string. In the
    /// legacy update entry point this argument immediately preceded the gateway password
    /// (<c>PortalController.vb</c> line 1568); here it does not, so the two must not be assumed to
    /// travel together.
    /// </remarks>
    public string? ProcessorUserId { get; set; }

    /// <summary>
    /// Gets or sets how much site-activity history the portal retains, in DAYS.
    /// </summary>
    /// <remarks>
    /// Days, not rows: the legacy screen labelled the field "Site Log History (Days):". Nullable
    /// because a portal need not have a retention period recorded.
    /// </remarks>
    public int? SiteLogHistory { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the page hosting the portal's administration menu.
    /// </summary>
    /// <remarks>
    /// Zero is a legitimate page identifier; see the note above the role identifiers. Only the
    /// identifier is carried - a caller needing the page itself fetches it from the pages
    /// resource.
    /// </remarks>
    public int? AdminTabId { get; set; }

    // MIGRATION: SuperTabId is not a portal attribute and is not per-portal at all. The view
    // computes it with an UNCORRELATED sub-select that names no portal, at
    // 04.05.00.SqlDataProvider line 1583:
    //
    //     (SELECT TOP 1 TabID FROM ...Tabs WHERE (PortalID IS NULL) AND (ParentId IS NULL))
    //
    // The predicate selects the host-level root page - the one page belonging to no portal and
    // having no parent - so the sub-select yields THE SAME VALUE FOR EVERY PORTAL. It is reported
    // for parity with the legacy object, it is read-only, and it must not be mistaken for
    // something a portal owns or an administrator can change.

    /// <summary>
    /// Gets or sets the identifier of the host-level root page. Read-only, and identical for every
    /// portal; see the note above.
    /// </summary>
    public int? SuperTabId { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the page shown to a first-time visitor ahead of the portal's
    /// home page.
    /// </summary>
    /// <remarks>
    /// Nullable because a portal need not define one. Zero is a legitimate page identifier; see
    /// the note above the role identifiers.
    /// </remarks>
    public int? SplashTabId { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the portal's home page.
    /// </summary>
    /// <remarks>
    /// Nullable because a portal need not nominate one explicitly. Zero is a legitimate page
    /// identifier; see the note above the role identifiers.
    /// </remarks>
    public int? HomeTabId { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the page carrying the portal's sign-in form.
    /// </summary>
    /// <remarks>
    /// Nullable because a portal need not nominate a dedicated one. Zero is a legitimate page
    /// identifier; see the note above the role identifiers.
    /// </remarks>
    public int? LoginTabId { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the page carrying the portal's user-account form.
    /// </summary>
    /// <remarks>
    /// Nullable because a portal need not nominate a dedicated one. Zero is a legitimate page
    /// identifier; see the note above the role identifiers.
    /// </remarks>
    public int? UserTabId { get; set; }

    /// <summary>
    /// Gets or sets the culture code the portal presents by default.
    /// </summary>
    /// <remarks>
    /// Transported as stored, neither parsed nor normalised, so an empty value may legitimately
    /// appear where a modern consumer would expect none.
    /// </remarks>
    public string? DefaultLanguage { get; set; }

    // MIGRATION: the member below keeps the legacy property's spelling, TimeZoneOffset with a
    // capital Z (PortalInfo.vb line 372), rather than the column's, which the view projects as
    // TimezoneOffset (04.05.00.SqlDataProvider line 1576). The two never conflicted because
    // database identifiers are matched without regard to case, whereas C# and the client model are
    // case-sensitive, so one spelling had to be chosen. The column itself is untouched and the
    // entity configuration binds the stored spelling.

    /// <summary>
    /// Gets or sets the portal's offset from Coordinated Universal Time, in MINUTES.
    /// </summary>
    /// <remarks>
    /// Minutes rather than hours, and necessarily so, because several real zones are offset by a
    /// fraction of an hour; the legacy time-conversion helper added the stored value directly as
    /// minutes (<c>Library/Components/Users/UserTime.vb</c> lines 33 and 63). A negative value is
    /// ordinary here - zones west of the prime meridian are negative - which means the legacy
    /// absent-value encoding, which the legacy code did apply to this field
    /// (<c>PortalSettings.vb</c> line 670), is indistinguishable from a genuine offset of one
    /// minute west. This contract transports whichever it is handed and converts in neither
    /// direction.
    /// </remarks>
    public int? TimeZoneOffset { get; set; }

    /// <summary>
    /// Gets or sets the portal's home directory, relative to the application, under which its
    /// uploaded content is kept.
    /// </summary>
    /// <remarks>
    /// Portal-relative, and that is the whole of what this contract reports: the legacy class also
    /// derived an absolute server path from it, and that derived property is absent here for the
    /// reason recorded at the head of this class. Resolving the value against a physical location
    /// is a server concern and stays on the server.
    /// </remarks>
    public string? HomeDirectory { get; set; }

    // MIGRATION: the legacy hand-written pre-generic alias dictionary keyed by lower-cased alias
    // (Library/Components/Portal/PortalAliasCollection.vb) produces no counterpart type - a
    // read-only generic list expresses the same thing natively. Read-only rather than mutable, an
    // array or a lazy sequence, so a received payload cannot be mutated in place and cannot hide
    // deferred work behind an enumeration.

    /// <summary>
    /// Gets or sets the host names through which this portal is reached, when they were loaded for
    /// this response.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No legacy counterpart; the legacy screens fetched aliases separately. Composes
    /// <see cref="PortalAliasDto"/>, declared alongside this contract.
    /// </para>
    /// <para>
    /// THREE STATES, and the first two must not be collapsed. No value means the aliases were not
    /// requested or not loaded, and says nothing about how many the portal has - nullability
    /// exists so a service may skip the alias query on the paths that do not need it without the
    /// payload misreporting the result. An empty collection means they were loaded and there are
    /// none. A populated collection means they were loaded and these are they. When no value is
    /// present a caller obtains them from the dedicated sub-resource
    /// <c>GET /api/v1/portals/{id}/aliases</c>, which an implementer is obliged to expose as the
    /// authoritative place to list, add and remove aliases.
    /// </para>
    /// </remarks>
    public IReadOnlyList<PortalAliasDto>? Aliases { get; set; }
}
