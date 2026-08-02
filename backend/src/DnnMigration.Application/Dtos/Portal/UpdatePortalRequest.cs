using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Dtos.Portal;

/// <summary>
/// Inbound request contract for <c>PUT /api/v1/portals/{id}</c>, carrying the complete set of
/// portal attributes that the legacy site-settings screen was able to modify.
/// </summary>
/// <remarks>
/// <para>
/// This type replaces the twenty-seven positional arguments of the legacy
/// <c>PortalController.UpdatePortalInfo</c> overload declared at
/// <c>Library/Components/Portal/PortalController.vb:L1568</c>. Every one of those twenty-seven
/// arguments appears below as a named property, in the order the signature declared them, and
/// nothing has been added to or removed from that set. The member set was verified twice from two
/// structurally independent places in the legacy source: the signature itself, and the
/// single-argument forwarding overload at <c>PortalController.vb:L1524</c>, which passes the same
/// twenty-seven entity members to it in the same order.
/// </para>
/// <para>
/// The request is consumed by <c>PortalsController</c> and handed to
/// <c>PortalService.UpdatePortalAsync</c>. That method answers with
/// <c>Result&lt;PortalDetailDto&gt;</c>, which carries success, the updated portal and the reason for
/// a failure as distinct data, and <c>PortalsController</c> translates it into an HTTP status code
/// and body. No member below describes an outcome, a status, a concurrency token or an error: an
/// update request describes only the desired state.
/// </para>
/// <para>
/// The subject is carried by the route and by nothing else. The <c>{id}</c> segment of
/// <c>PUT /api/v1/portals/{id}</c> identifies the portal being updated, and it is the sole source of
/// that identifier: no member below carries a copy of it. An earlier revision did carry one, on the
/// ground that the replacement of the legacy signature was one-for-one, and left the resulting
/// route-versus-body disagreement to be resolved elsewhere. That is an authorisation risk whose
/// safety depends on a comparison nobody is obliged to perform, so the duplicate was removed instead;
/// the reasoning is recorded in full at the point where the member used to be declared, and the
/// divergence is listed in <c>MIGRATION_NOTES.md</c>. There is consequently no identity comparison for
/// <c>Application/Validation/UpdatePortalRequestValidator.cs</c> to make - the class of defect is
/// closed by the shape of the contract rather than by a rule.
/// </para>
/// <para>
/// Deliberately not updatable. The legacy entity
/// <c>Library/Components/Portal/PortalInfo.vb</c> declares thirty-nine public properties. The set
/// difference against the twenty-seven arguments is exactly twelve members, and none of them may be
/// added to this request: <c>AdministratorRoleId</c> (PortalInfo.vb:L189),
/// <c>AdministratorRoleName</c> (L197), <c>RegisteredRoleId</c> (L205), <c>RegisteredRoleName</c>
/// (L213), <c>GUID</c> (L245), <c>Email</c> (L285), <c>AdminTabId</c> (L293), <c>SuperTabId</c>
/// (L301), <c>Users</c> (L309), <c>Pages</c> (L320), <c>HomeDirectoryMapPath</c> (L388) and
/// <c>Version</c> (L395). Each is excluded for a measured reason, recorded in the migration note
/// that opens the class body.
/// </para>
/// <para>
/// Units and semantics, taken from the authoritative English wording in
/// <c>Website/admin/Portal/App_LocalResources/SiteSettings.ascx.resx</c>:
/// <see cref="HostFee"/> is a monthly monetary hosting charge denominated in <see cref="Currency"/>;
/// <see cref="HostSpace"/> is a disk-space quota in whole megabytes; <see cref="PageQuota"/> and
/// <see cref="UserQuota"/> are counts; <see cref="SiteLogHistory"/> is a retention period in days;
/// and <see cref="TimeZoneOffset"/> is an offset from co-ordinated universal time in minutes.
/// </para>
/// <para>
/// Measured validation rule set. This type declares no rule and enforces none, and the rules that do
/// exist are far thinner than a reader might assume. The entire legacy screen
/// <c>Website/admin/Portal/sitesettings.ascx</c> carries exactly two validators, and both are a
/// comparison validator performing a data-type check: <c>valExpiryDate</c> at L433
/// (<c>Operator="DataTypeCheck" Type="Date"</c>, message "Invalid expiry date!") and
/// <c>valHostFee</c> at L444 (<c>Operator="DataTypeCheck" Type="Currency"</c>, message "Invalid fee,
/// needs to be a currency value!"). There is <b>no</b> required-field validator anywhere on the
/// screen, and <c>txtHostSpace</c> carries no validator at all. The legacy update path therefore
/// required nothing. The validator author must reproduce that measured rule set exactly and must not
/// invent rules; the terminal column lengths documented on each property below are the only other
/// constraint the schema imposes.
/// </para>
/// <para>
/// Absent values at the boundary. The legacy null contract in
/// <c>Library/Components/Shared/Null.vb</c> does not use database nulls in memory. It substitutes a
/// per-type sentinel on every read and converts that sentinel back to a database null on every
/// write: its absent-integer sentinel is the value -1 (L41-L45, converted back at L167-L169), its
/// absent-string sentinel is the empty string rather than a null reference (L71-L75, converted back
/// at L192-L193), and its absent-date sentinel is the minimum date value (L66-L70). Modern nullable
/// types replace those sentinels, so an absent value arrives here as <c>null</c>. No property below
/// converts between the two representations, and none treats any particular numeric or string value
/// as meaning absent; the sentinel semantics are documented at this boundary and are never allowed
/// to reach the domain model.
/// </para>
/// <para>
/// Identifiers are never tested for absence by their value, because in this schema no such test is
/// sound. <c>Portals.PortalID</c> is declared <c>IDENTITY (-1, 1)</c> at
/// <c>Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider:L77</c>, so the same
/// negative value that the legacy contract uses to mean absent is also a real, addressable portal
/// identifier; the stock <c>_default</c> portal then occupies the very next value, zero, inserted at
/// <c>01.00.00.SqlDataProvider:L7125</c>. <c>Tabs.TabID</c> (L140) and <c>Roles.RoleID</c> (L115)
/// are likewise seeded at zero, so zero is a legitimate tab and role identifier. Zero is a
/// meaningful value for non-identifier members too: it means unlimited disk space for
/// <see cref="HostSpace"/> and it is the offset of the United Kingdom for
/// <see cref="TimeZoneOffset"/>. Presence is therefore expressed only by nullability.
/// </para>
/// <para>
/// The Option Strict asymmetry. <c>Library/DotNetNuke.Library.vbproj:L24</c> compiles the class
/// library with <c>OptionStrict On</c>, whereas <c>Website/release.config:L125</c> compiles the web
/// pages with <c>strict="false"</c>. The legacy code-behind that calls the replaced signature is
/// therefore permitted implicit narrowing conversions that C# rejects outright, and every one of
/// them has been made explicit in the property documentation below rather than reproduced. The
/// conversions measured on the save path of <c>Website/admin/Portal/SiteSettings.ascx.vb</c> are
/// itemised in the migration note that opens the class body.
/// </para>
/// <para>
/// Legacy source of truth. The signature and its forwarding overload were read from
/// <c>Library/Components/Portal/PortalController.vb</c>; the property types from
/// <c>Library/Components/Portal/PortalInfo.vb</c>; the terminal column types from the
/// eighty-eight-script chain under
/// <c>Website/Providers/DataProviders/SqlDataProvider/</c>; the field set, controls and validators
/// from <c>Website/admin/Portal/sitesettings.ascx</c> (note the lower-case markup filename, beside a
/// PascalCase code-behind); the workflow from
/// <c>Website/admin/Portal/SiteSettings.ascx.vb</c>; and the label wording and units from
/// <c>Website/admin/Portal/App_LocalResources/SiteSettings.ascx.resx</c>.
/// </para>
/// </remarks>
public sealed class UpdatePortalRequest
{
    // MIGRATION: the legacy UpdatePortalInfo is a VB Sub, not a Function -- it returns nothing at
    //   all (PortalController.vb:L1568), and its body simply forwards the twenty-seven arguments to
    //   the data provider and then clears the portal cache. A caller could not learn whether the
    //   update had succeeded, which portal had been written, or why a write had failed. The modern
    //   replacement carries the outcome in the result object returned by
    //   PortalService.UpdatePortalAsync, where success, the updated portal and the failure reason
    //   are distinct data. Nothing on this request encodes an outcome.
    //
    //   A second legacy overload, UpdatePortalInfo(ByVal Portal As PortalInfo) at
    //   PortalController.vb:L1524, accepted the persistence entity itself and forwarded its
    //   twenty-seven members to the signature above. That overload is deliberately NOT reproduced:
    //   no entity crosses the wire in either direction, which is precisely what allows the legacy
    //   sentinel semantics to be honoured at this boundary without contaminating the domain model.
    //   Its existence is recorded here because it independently corroborates both the member set
    //   and its ordering.

    // MIGRATION: the legacy entity carried XML serialisation decoration that is dropped entirely.
    //   PortalInfo.vb:L26 imports the serialisation namespace, L29 declares the class as an XML
    //   root named "settings", and every property except GUID and HomeDirectoryMapPath carries an
    //   element attribute naming a lower-case wire field. None of it is ported. This request owns
    //   its own wire contract and declares no serialisation, binding, mapping or validation
    //   attribute of any kind; the transport representation is configured once at the API layer.

    // MIGRATION: twelve of the thirty-nine public properties on PortalInfo.vb are deliberately
    //   absent from this request, because the legacy update path never wrote them. Each reason was
    //   measured rather than assumed:
    //
    //     Email (PortalInfo.vb:L285) is not a Portals column at all. Two independent
    //       case-insensitive searches of the eighty-eight-script chain for a Portals column of that
    //       name returned nothing. It reaches the legacy entity only through the terminal read
    //       view, whose definition ends "FROM ...Portals AS P LEFT OUTER JOIN ...Users AS U ON
    //       P.AdministratorId = U.UserID" (04.05.00.SqlDataProvider), so the value is the
    //       ADMINISTRATOR's address, carried on the joined user row. That is also why the legacy
    //       property carries no length, format or requiredness decoration whatsoever, unlike the
    //       equivalent property on the user entity. Changing an administrator's address is a user
    //       operation, reachable through the user resource, not a portal update.
    //     SuperTabId (L301), AdministratorRoleName (L197) and RegisteredRoleName (L213) are
    //       computed sub-selects in that same view -- the host root tab, and the display names
    //       looked up from the roles table by identifier. They are derived on read and can never be
    //       written.
    //     AdminTabId (L293), AdministratorRoleId (L189) and RegisteredRoleId (L205) are genuine
    //       Portals columns, but the replaced signature does not accept them and the legacy screen
    //       renders no control for them. They are established when a portal is provisioned and
    //       thereafter maintained by role and page administration.
    //     GUID (L245) is declared uniqueidentifier NOT NULL with a DEFAULT (newid()) constraint at
    //       01.00.00.SqlDataProvider:L92. It is immutable identity, shown read-only on the legacy
    //       screen through a label control, and is never client-writable.
    //     Version (L395) does not belong to Portals. The only nvarchar(8) column of that name in
    //       the entire chain is at 02.00.00.SqlDataProvider:L5144, inside the CREATE TABLE for
    //       DesktopModules (L5141). It is absent from the portal read view.
    //     Users (L309) and Pages (L320) are not persisted at all. Both are lazy getters that count
    //       rows on demand -- the first through the membership provider's user-count query
    //       (Library/Providers/MembershipProviders/DataProvider/DataProvider.vb:L82), the second
    //       through the page controller's tab count -- and both are absent from the read view. A
    //       count is computed, never submitted.
    //       Both declare a setter on the legacy entity (PortalInfo.vb:L316 and L327), so neither is
    //       read-only there; the exclusion rests on the stronger ground that neither is a column.
    //     HomeDirectoryMapPath (L388) is the single read-only property on the legacy entity. It
    //       derives an absolute server filesystem path, reaching into two subsystems this migration
    //       excludes -- the static utility module for the application path, and the file-system
    //       services for the mapped-directory lookup. Accepting a server filesystem path from an
    //       HTTP client would additionally be a path-traversal hazard. The relative
    //       HomeDirectory below is the writable member; the absolute path is derived server-side.
    //
    //   The legacy screen also renders controls for features excluded from this migration -- the
    //   search provider, the inline editor, the secure-transport settings, the skinning settings,
    //   the site-map and site-ownership hooks, and the three administration-bar settings. None of
    //   them appears among the twenty-seven arguments, which independently confirms that none is a
    //   Portals column reachable by this work, and none appears below.

    // MIGRATION: the legacy code-behind compiled with Option Strict disabled
    //   (Website/release.config:L125) and its save path at
    //   Website/admin/Portal/SiteSettings.ascx.vb:L704-L753 relies on coercions that C# rejects.
    //   Each is recorded here, made explicit, and left for the service and validator to honour:
    //
    //     A blank fee box left the local at its initialiser of zero rather than at the legacy
    //       absent-number sentinel, so "no fee entered" was persisted as zero, not as a database
    //       null (L704-L707).
    //     A blank disk-space box behaved identically (L709-L712) -- and that local is declared As
    //       Double even though the terminal column is an integer, so a fractional entry parsed
    //       successfully, travelled as a floating-point value, and was silently truncated by the
    //       database at the integer procedure parameter. This is a place where the coerced result
    //       genuinely differs, and it is why the modern property is an integer.
    //     A blank page-quota box left the local at zero (L714-L717).
    //     The user-quota local is declared As Double at L719 despite its integer-suggesting name,
    //       is assigned from an integer parse at L721, and is then passed to an argument declared
    //       As Integer -- an implicit floating-point to integer narrowing that C# rejects outright.
    //       This is the clearest single artefact of the disabled strictness on this screen.
    //     A blank site-log-history box left the local at the legacy absent-integer sentinel value
    //       of -1, written as a bare literal rather than through the null contract (L724-L727).
    //     A blank expiry box left the local at the legacy absent-date sentinel (L729-L732).
    //     All four page-reference locals default to the legacy absent-integer sentinel when no
    //       list item is selected (L734, L739, L744, L749).
    //     The payment-processor argument is built from a conditional-expression function whose
    //       result is an untyped object, on which a late-bound string conversion is then invoked
    //       (L777). Both arms are free of side effects, so a short-circuiting conditional is an
    //       exact equivalent; the conversion is explicit in the modern contract because the
    //       property is typed.
    //
    //   No coercion, default, clamp or parse is performed by this type. It is an inert carrier.

    // MIGRATION: six of the twenty-seven members are host-only, and the rule is enforced neither
    //   here nor by the transport. SiteSettings.ascx.vb:L760-L770 compares the submitted values of
    //   the hosting fee, the disk-space quota, the page quota, the user quota, the site-log
    //   retention and the expiry date against the stored portal and rejects the entire save with an
    //   exception when a caller who is not a super user has altered any of them. That is a live
    //   authorisation rule over this request's contents and it must be reproduced in
    //   PortalService.UpdatePortalAsync and mirrored in the Angular portal-settings form. It is
    //   deliberately absent from this type, which validates nothing and authorises nothing.

    // MIGRATION: LEGACY ARGUMENT 1, PortalId, IS DELIBERATELY NOT A MEMBER OF THIS CONTRACT, and its
    // removal is a considered decision rather than an omission. The argument itself is preserved - it
    // is the {id} segment of PUT /api/v1/portals/{id} and the first parameter of
    // IPortalService.UpdatePortalAsync(int, UpdatePortalRequest, CancellationToken) - so nothing about
    // the operation is lost. What is removed is a SECOND, caller-controlled copy of the same key
    // inside the body.
    //
    // An earlier revision carried the copy and documented that the route segment was authoritative,
    // that the two could disagree, and that a comparison "belongs to the controller or the validator".
    // That arrangement is the defect: a subject key supplied twice, with the mismatch handled by a
    // rule declared somewhere else, is an authorisation risk that depends for its safety on a check
    // nobody is forced to invoke. The two candidate fixes are enforcing equality or removing the
    // duplicate, and removal is chosen because it is the only one that cannot be forgotten. Enforcing
    // equality is mechanically possible - Api/Filters/FluentValidationActionFilter.cs publishes every
    // route value into the validation context's root data, so a rule could read the route identifier -
    // but a rule protects only the requests that actually reach it, and only for as long as nobody adds
    // an entry point that binds the body without it. With no second copy in the body there is nothing
    // to disagree on any path, and the class of defect is closed by construction rather than by
    // vigilance.
    //
    // Legacy provenance, retained because it explains where the argument came from: the column is
    // Portals.PortalID, declared [int] IDENTITY (-1, 1) NOT NULL at 01.00.00.SqlDataProvider:L77; the
    // legacy code-behind supplied the argument from page state rather than from a control, and the
    // forwarding overload at PortalController.vb:L1524 supplied it from the entity's own PortalID
    // member. The identity seed is the same negative value the legacy contract used as its
    // absent-integer sentinel, and the stock _default portal occupies zero
    // (01.00.00.SqlDataProvider:L7125), so both are real addressable identifiers and no route value
    // may be rejected on the ground that it looks like an "absent" marker.
    //
    // This also makes the update contract consistent with CreatePortalRequest, which carries no
    // identifier at all.

    /// <summary>
    /// Gets or sets the display name of the portal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy argument 2, <c>PortalName</c>, typed <c>String</c>, supplied by the
    /// <c>txtPortalName</c> text box and passed as the second argument at
    /// <c>SiteSettings.ascx.vb:L772</c>. The backing column is declared
    /// <c>[nvarchar] (128) NOT NULL</c> at <c>01.00.00.SqlDataProvider:L79</c>.
    /// </para>
    /// <para>
    /// Measured rules: maximum length 128, and the column rejects a database null. The legacy
    /// screen declared no validator on the control, so requiredness was never enforced at the
    /// presentation layer even though the column demands a value. The validator reproduces the
    /// measured rules; an empty string may arrive here where a modern reader would expect
    /// <c>null</c>, because the legacy contract represented an absent string as the empty string.
    /// </para>
    /// </remarks>
    public string? PortalName { get; set; }

    // MIGRATION: the logo reference is asymmetric between reading and writing, and echoing a read
    //   value back into this request destroys data. The terminal read path is the portal view, not
    //   the Portals table: the GetPortal procedure at 04.04.00.SqlDataProvider:L199 is
    //   "SELECT * FROM ...vw_Portals WHERE PortalId = @PortalId". That view rewrites this column at
    //   04.05.00.SqlDataProvider:L1535 --
    //
    //     CASE WHEN LEFT(LOWER(LogoFile), 6) = 'fileid'
    //          THEN (SELECT Folder + FileName FROM ...Files
    //                WHERE 'fileid=' + convert(varchar, ...Files.FileID) = LogoFile)
    //          ELSE LogoFile END AS LogoFile
    //
    //   so the STORED value may be a "fileid=NNN" token while the value the API RETURNS is the
    //   resolved folder-and-filename path that token points at. What this property accepts is
    //   written to the column verbatim. A client that reads a portal and writes the same logo value
    //   back therefore replaces the token with a resolved path and PERMANENTLY BREAKS the managed
    //   file link, silently and unrecoverably. Callers must send either a "fileid=NNN" token or a
    //   path deliberately, and never a blind echo of a read value.
    //
    //   Token detection, resolution and preservation are deliberately not implemented here: the
    //   file-system subtree that owns the managed-file abstraction is excluded from this migration,
    //   and the asymmetry is fully expressible as a nullable string plus this note. A dedicated
    //   file-reference type was considered during planning and rejected on the same grounds.

    /// <summary>
    /// Gets or sets the reference to the portal logo image.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy argument 3, <c>LogoFile</c>, typed <c>String</c>. It was supplied by the
    /// <c>ctlLogo</c> picker control declared at <c>sitesettings.ascx:L140</c>, whose selected
    /// value the code-behind read into a local before passing it as the third argument
    /// (<c>SiteSettings.ascx.vb:L698</c>, call site L772). The backing column is declared
    /// <c>[nvarchar] (50) NULL</c> at <c>01.00.00.SqlDataProvider:L81</c>, and the
    /// <c>Tmp_Portals</c> rebuild carries the same width forward at
    /// <c>01.00.05.SqlDataProvider:L1369</c>, so 50 is terminal. The citation previously read L80,
    /// which is the adjacent <c>[UploadDirectory] [nvarchar] (100) NOT NULL</c> column - a different
    /// column of a different width and nullability. The line was verified by reading the baseline
    /// <c>CREATE TABLE [dbo].[Portals]</c> block at L76-L93 in full.
    /// </para>
    /// <para>
    /// The value is either a managed-file token of the form <c>fileid=NNN</c> or a path, and the two
    /// are not interchangeable. See the migration note immediately above this property: the read
    /// and write representations differ, and echoing a read value back here breaks the file link.
    /// </para>
    /// <para>
    /// Measured rules: maximum length 50, which is narrow enough that a resolved folder-and-filename
    /// path can exceed it where a token would not. The length rule belongs to the validator, not to
    /// this type. The legacy screen declared no validator on the control. An empty string and
    /// <c>null</c> are indistinguishable in legacy data.
    /// </para>
    /// </remarks>
    public string? LogoFile { get; set; }

    /// <summary>
    /// Gets or sets the text rendered in the portal footer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy argument 4, <c>FooterText</c>, typed <c>String</c>, supplied by the
    /// <c>txtFooterText</c> text box. The backing column is declared <c>[nvarchar] (100) NULL</c> at
    /// <c>01.00.00.SqlDataProvider:L82</c>, and the <c>Tmp_Portals</c> rebuild carries it forward
    /// unchanged at <c>01.00.05.SqlDataProvider:L1370</c> with no later <c>ALTER COLUMN</c>, so that
    /// declaration is terminal. The stock <c>_default</c> portal ships with a copyright
    /// notice in this column (<c>01.00.00.SqlDataProvider:L7125</c>).
    /// </para>
    /// <para>
    /// Measured rules: maximum length 100. The legacy screen declared no validator on the control.
    /// An empty string may arrive where a modern reader would expect <c>null</c>.
    /// </para>
    /// </remarks>
    public string? FooterText { get; set; }

    // MIGRATION: the legacy absent-date sentinel is the minimum date value, not a database null, and
    //   the comparison that recognises it is DATE-ONLY. Library/Components/Shared/Null.vb:L66-L70
    //   returns the minimum date as its absent-date sentinel; the absence test at L231-L232 compares
    //   only the date component ("objDate.Date.Equals(...Date)"), and -- a detail worth recording
    //   because it is easy to miss -- the write-side conversion at L184-L186 compares date-only as
    //   well. Any instant whose date component is the minimum date was therefore treated as absent
    //   regardless of its time component, on reads AND on writes. The legacy code-behind relied on
    //   exactly this: a blank expiry box left its local at that sentinel
    //   (SiteSettings.ascx.vb:L729-L732), which the data layer then stored as a database null.
    //
    //   A null value here is the modern expression of "no expiry". This property performs NO
    //   conversion: a minimum-date instant is passed through unchanged rather than being silently
    //   reinterpreted as absent, so the divergence is visible to the service instead of being
    //   absorbed by the wire contract.

    /// <summary>
    /// Gets or sets the instant at which the portal's hosting arrangement expires, or <c>null</c>
    /// when the portal does not expire.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy argument 5, <c>ExpiryDate</c>, typed <c>Date</c> -- the only date-typed argument in the
    /// replaced signature. It was supplied by the <c>txtExpiryDate</c> text box, assisted by a
    /// pop-up calendar (<c>SiteSettings.ascx.vb:L246</c>) and rendered on read through a short-date
    /// conversion (L342). The backing column is declared <c>[datetime] NULL</c> at
    /// <c>01.00.00.SqlDataProvider:L82</c>, and the stock <c>_default</c> portal ships with a
    /// database null in it.
    /// </para>
    /// <para>
    /// The property is nullable because the column is nullable. See the migration note above for the
    /// minimum-date sentinel and its date-only comparison semantics.
    /// </para>
    /// <para>
    /// Measured rules: one of only two validators on the entire legacy screen guards this control --
    /// <c>valExpiryDate</c> at <c>sitesettings.ascx:L433</c>, a comparison validator with
    /// <c>Operator="DataTypeCheck" Type="Date"</c> and the message "Invalid expiry date!". It is a
    /// well-formedness check, not a range check and not a requiredness check.
    /// </para>
    /// <para>
    /// This is one of six host-only members. A caller who is not a super user may not change it; see
    /// the host-only migration note in this class.
    /// </para>
    /// </remarks>
    public DateTime? ExpiryDate { get; set; }

    /// <summary>
    /// Gets or sets the mode by which the portal admits new user accounts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy argument 6, <c>UserRegistration</c>, typed <c>Integer</c> -- an untyped discriminator
    /// that becomes the named <see cref="UserRegistrationMode"/> here, because magic integers become
    /// named enumeration members. The backing column reaches a terminal shape of
    /// <c>int NOT NULL</c> with a database default of zero, so the property is deliberately not
    /// nullable: there is no absent case to model, and zero is a genuine chosen mode rather than a
    /// stand-in for a missing value.
    /// </para>
    /// <para>
    /// The ordinals are live persisted data. The legacy screen bound the stored integer straight to
    /// the zero-based selected index of the <c>optUserRegistration</c> radio-button list
    /// (<c>SiteSettings.ascx.vb:L277</c>) and wrote that index straight back as this argument
    /// (L773), so the option list at <c>sitesettings.ascx:L230-L233</c> is the authoritative member
    /// set: value 0 "None", 1 "Private", 2 "Public", 3 "Verified". The stock <c>_default</c> portal
    /// ships with value 2 (<c>01.00.00.SqlDataProvider:L7125</c>).
    /// </para>
    /// <para>
    /// Wire representation. An enumeration is serialised and deserialised as its numeric value by
    /// default, which is byte-identical to the persisted column and to what the legacy radio list
    /// posted. No converter attribute is declared, precisely so that the behaviour-preserving
    /// default is what applies. An integer outside the declared range simply fails validation.
    /// </para>
    /// <para>
    /// Measured rules: the legacy screen declared no validator on the control, because a radio-button
    /// list cannot submit a value it does not offer. The validator should confirm the value is a
    /// declared member.
    /// </para>
    /// </remarks>
    public UserRegistrationMode UserRegistration { get; set; }

    /// <summary>
    /// Gets or sets whether the portal shows no banner advertising, serves its own, or defers to
    /// host-managed advertising.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy argument 7, <c>BannerAdvertising</c>, typed <c>Integer</c>, replaced by the named
    /// <see cref="BannerAdvertisingMode"/>. The backing column reaches a terminal shape of
    /// <c>int NOT NULL</c> with a database default of zero, so the property is not nullable. The
    /// member set comes from the <c>optBanners</c> option list at
    /// <c>sitesettings.ascx:L123-L125</c> -- value 0 "None", 1 "Site", 2 "Host" -- which the legacy
    /// screen bound as a selected index (<c>SiteSettings.ascx.vb:L291</c>) and wrote back as this
    /// argument (L774). The stock <c>_default</c> portal ships with value 0.
    /// </para>
    /// <para>
    /// A live business rule attaches to <see cref="BannerAdvertisingMode.Host"/> and must be
    /// enforced in <c>PortalService.UpdatePortalAsync</c>, mirrored in the Angular
    /// portal-settings form, and <b>not</b> in this type.
    /// <c>Website/admin/Portal/SiteSettings.ascx.vb:L295</c> evaluates
    /// <c>optBanners.Enabled = objPortal.BannerAdvertising &lt;&gt; 2</c> and the following line makes
    /// an explanatory label visible for the same mode, so when the stored mode is host-managed the
    /// portal administrator could not change the setting at all. Both checks are taken only inside
    /// the branch for a caller who is not a super user (L292-L297), because a super user is the host
    /// and edits the setting freely. This property declares no flag describing editability and
    /// rejects no value in its setter.
    /// </para>
    /// <para>
    /// Wire representation: numeric, by the same reasoning as
    /// <see cref="UserRegistration"/>. No converter attribute is declared. Measured rules: none on
    /// the legacy screen; the validator should confirm the value is a declared member.
    /// </para>
    /// </remarks>
    public BannerAdvertisingMode BannerAdvertising { get; set; }

    /// <summary>
    /// Gets or sets the ISO currency code in which <see cref="HostFee"/> is denominated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy argument 8, <c>Currency</c>, typed <c>String</c>, supplied by the <c>cboCurrency</c>
    /// list and passed as the selected item's value (<c>SiteSettings.ascx.vb:L775</c>). The backing
    /// column is declared <c>[char] (3) NULL</c> at <c>01.00.00.SqlDataProvider:L88</c>, carried
    /// forward unchanged by the <c>Tmp_Portals</c> rebuild at
    /// <c>01.00.05.SqlDataProvider:L1376</c>, and the stock <c>_default</c> portal ships with "USD".
    /// The citation previously read L87, which is the adjacent
    /// <c>[PayPalId] [nvarchar] (50) NULL</c> column. The line was verified by reading the baseline
    /// <c>CREATE TABLE [dbo].[Portals]</c> block at L76-L93 in full.
    /// </para>
    /// <para>
    /// Measured rules: exactly three characters, imposed by the fixed-width column rather than by any
    /// validator -- the legacy screen declared none, because a drop-down list cannot submit a value it
    /// does not offer. Note that a fixed-width column pads on read, so a three-character code is
    /// returned unpadded only because the code fills the column exactly. An empty string may arrive
    /// where a modern reader would expect <c>null</c>.
    /// </para>
    /// </remarks>
    public string? Currency { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the user who administers the portal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy argument 9, <c>AdministratorId</c>, typed <c>Integer</c>, supplied by the
    /// <c>cboAdministratorId</c> list through an explicit integer conversion of the selected item's
    /// value (<c>SiteSettings.ascx.vb:L776</c>). The backing column is declared <c>[int] NULL</c> at
    /// <c>01.00.00.SqlDataProvider:L86</c> - previously cited as L85, which is the adjacent
    /// <c>[BannerAdvertising] [int] NULL</c> column - and is the foreign key the terminal read view joins on to
    /// project the administrator's electronic-mail address onto the portal row.
    /// </para>
    /// <para>
    /// The property is nullable because the column is nullable; <c>null</c> means no administrator is
    /// assigned. <c>Users.UserID</c> is declared <c>IDENTITY (1, 1)</c> at
    /// <c>01.00.00.SqlDataProvider:L98</c>, so zero never occurs as a user identifier -- but that is
    /// recorded as schema knowledge only. No positivity guard is declared here, because this type
    /// validates nothing, and no value is treated as meaning absent.
    /// </para>
    /// <para>
    /// Measured rules: none on the legacy screen. Referential integrity against the users table
    /// belongs to the service and to the database, not to this contract.
    /// </para>
    /// </remarks>
    public int? AdministratorId { get; set; }

    // MIGRATION: the hosting fee has THREE conflicting source types, and the schema type wins.
    //
    //     Library/Components/Portal/PortalInfo.vb:L157 declares the entity property "As Single".
    //     PortalController.vb:L1568 declares argument 10 of the replaced signature "As Double".
    //     The terminal column type is SQL money, NOT NULL, defaulting to zero.
    //
    //   Rule T4 makes the schema authoritative, because entity mapping binds to the terminal column
    //   discovered in the eighty-eight-script chain, so the modern type is a nullable decimal. The
    //   floating-point choice is the trap here and it is deliberately declined twice: not the
    //   single-precision type the entity used, and not the double-precision type this very signature
    //   used. Binary floating point cannot represent a monetary value exactly, and money is a
    //   fixed-point currency type.
    //
    //   The terminal type was measured, not inferred from the baseline table. The baseline
    //   CREATE TABLE at 01.00.00.SqlDataProvider:L76-L93 declares this column nvarchar(10) -- a
    //   STRING -- and is not the terminal schema: there are 98 "ALTER TABLE ... Portals" statements
    //   across the chain. The baseline is independently untrustworthy, since it also declares an
    //   upload-directory column and a payment-identifier column, neither of which is among the
    //   thirty-nine properties of the legacy entity. The single terminal redefinition is
    //   03.01.01.SqlDataProvider:L1118 --
    //
    //     ALTER TABLE {databaseOwner}{objectQualifier}Portals ALTER COLUMN [HostFee] [money] NOT NULL
    //
    //   with the zero default re-added at L1129. Corroboration: the widening migration at
    //   01.00.05.SqlDataProvider:L1412 carries "CONVERT(money, HostFee)", the stored procedures
    //   declare the parameter as money (including a form defaulting to zero), and the one validator
    //   the legacy screen placed on this field is a currency data-type check
    //   (sitesettings.ascx:L444). The resource file calls it "the monthly charge for hosting this
    //   site".
    //
    //   Nullability. The column is NOT NULL with a zero default while the legacy absent-number
    //   sentinel was the minimum value of the numeric type; the modern expression of absence is
    //   null, and the service supplies zero where a caller sends null. That substitution is
    //   DOCUMENTED here and implemented in PortalService, never in this property. Note also that the
    //   legacy code-behind never sent the sentinel: a blank box left its local at zero
    //   (SiteSettings.ascx.vb:L704-L707), so "no fee entered" was persisted as a real zero fee.
    //
    //   No clamp is applied. The conditional-expression clamps at PortalController.vb:L395 and L398
    //   guard a role's service and trial fees inside a role-creation helper (the entity is
    //   constructed at L390) and have nothing to do with this column; either way, clamping is
    //   service logic and never belongs in a property setter.

    /// <summary>
    /// Gets or sets the monthly monetary hosting charge for the portal, denominated in
    /// <see cref="Currency"/>, or <c>null</c> to leave the service to apply the schema default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy argument 10, <c>HostFee</c>. The resource file describes it as "The Hosting Fee is the
    /// monthly charge for hosting this site." It was supplied by the <c>txtHostFee</c> text box and
    /// rendered on read through a plain string conversion (<c>SiteSettings.ascx.vb:L344</c>).
    /// </para>
    /// <para>
    /// The modern type is a nullable decimal. See the migration note above for the three-way type
    /// conflict, the measured terminal column type and the reason a floating-point type is declined.
    /// </para>
    /// <para>
    /// Measured rules: one of only two validators on the entire legacy screen guards this control --
    /// <c>valHostFee</c> at <c>sitesettings.ascx:L444</c>, a comparison validator with
    /// <c>Operator="DataTypeCheck" Type="Currency"</c>, message "Invalid fee, needs to be a currency
    /// value!" and resource key <c>valHostFee.Error</c>. It is a well-formedness check only: there is
    /// no requiredness rule and no range rule, and in particular the legacy screen did not reject a
    /// negative fee.
    /// </para>
    /// <para>
    /// This is one of six host-only members. A caller who is not a super user may not change it; see
    /// the host-only migration note in this class.
    /// </para>
    /// </remarks>
    public decimal? HostFee { get; set; }

    // MIGRATION: the disk-space quota also has three conflicting source types, and here the measured
    //   schema CORRECTS the specification this file was written against.
    //
    //     Library/Components/Portal/PortalInfo.vb:L165 declares the entity property "As Integer".
    //     PortalController.vb:L1568 declares argument 11 of the replaced signature "As Double".
    //     The terminal column type is SQL int, NOT NULL, defaulting to zero.
    //
    //   The folder requirements assert that this field is SQL money and that the modern type should
    //   therefore be a nullable decimal, exactly as for the hosting fee. That premise is NOT
    //   supported by the schema. The single terminal redefinition is the line immediately after the
    //   fee's, 03.01.01.SqlDataProvider:L1119 --
    //
    //     ALTER TABLE {databaseOwner}{objectQualifier}Portals ALTER COLUMN [HostSpace] [int] NOT NULL
    //
    //   -- with the zero default added at 01.00.05.SqlDataProvider:L1401. Every stored-procedure
    //   parameter measured for this field is declared int (one form defaulting to null), never money,
    //   and the widening migration at 01.00.05.SqlDataProvider:L1412 passes this column through
    //   UNCONVERTED in the very statement that converts the fee to money. Applying the requirements'
    //   own stated tie-break -- the entity and schema type wins, which is Rule T4 -- yields a
    //   nullable integer for this member and a nullable decimal for the fee. The discrepancy is
    //   REPORTED here rather than silently corrected.
    //
    //   Two independent corroborations that this is a count and not a currency amount: the legacy
    //   screen placed NO validator at all on txtHostSpace, whereas it placed a currency data-type
    //   check on the fee; and the resource file reads "The amount of Disk Space in MB allowed for
    //   this site (enter 0 for unlimited space)" -- whole megabytes.
    //
    //   That wording also makes zero a MEANINGFUL value, not a stand-in for absence: zero means
    //   unlimited disk space. Nothing here may treat it as absent.
    //
    //   Nullability follows the same reasoning as the fee: the column is NOT NULL with a zero
    //   default, the legacy absent-number sentinel was the minimum value of the numeric type, null is
    //   the modern expression of absence, and the service supplies the default where a caller sends
    //   null. Documented, not implemented. The legacy code-behind again never sent the sentinel: a
    //   blank box left its local at zero (SiteSettings.ascx.vb:L709-L712) -- which, given the
    //   resource wording, silently meant "unlimited" rather than "none".
    //
    //   The integer type also closes the narrowing hazard described in the Option Strict note: the
    //   legacy local was declared double-precision even though the column is an integer, so a
    //   fractional entry parsed successfully and was truncated by the database at the integer
    //   procedure parameter. A fractional value now fails to bind rather than being silently
    //   truncated.

    /// <summary>
    /// Gets or sets the disk-space quota for the portal in whole megabytes, where zero means
    /// unlimited, or <c>null</c> to leave the service to apply the schema default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy argument 11, <c>HostSpace</c>. The resource file labels it "Disk Space:" and explains
    /// "The amount of Disk Space in MB allowed for this site (enter 0 for unlimited space)." It was
    /// supplied by the <c>txtHostSpace</c> text box and rendered on read through a plain string
    /// conversion (<c>SiteSettings.ascx.vb:L345</c>).
    /// </para>
    /// <para>
    /// The modern type is a nullable integer, which corrects the type this file's own specification
    /// predicted. See the migration note above for the measured evidence and the tie-break applied.
    /// </para>
    /// <para>
    /// Measured rules: <b>none</b>. Uniquely among the numeric fields on this screen,
    /// <c>txtHostSpace</c> carries no validator of any kind. The validator author must not infer a
    /// currency rule from the neighbouring hosting-fee validator, and must not infer a positivity
    /// rule, because zero is the documented value for unlimited space.
    /// </para>
    /// <para>
    /// This is one of six host-only members. A caller who is not a super user may not change it; see
    /// the host-only migration note in this class.
    /// </para>
    /// </remarks>
    public int? HostSpace { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of pages the portal may contain, or <c>null</c> when no page
    /// quota applies.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy argument 12, <c>PageQuota</c>, typed <c>Integer</c>, supplied by the
    /// <c>txtPageQuota</c> text box and rendered on read at <c>SiteSettings.ascx.vb:L346</c>. The
    /// backing column is a nullable integer, so the property is nullable. A page count is compared
    /// against this quota, so it is a count and not a size.
    /// </para>
    /// <para>
    /// This argument is one of four that the legacy method's own documentation header omitted: the
    /// header at <c>PortalController.vb:L1536-L1567</c> declares only twenty-three parameter tags for
    /// a twenty-seven-argument signature. That legacy documentation defect is recorded rather than
    /// carried forward -- every member of this request is documented.
    /// </para>
    /// <para>
    /// Measured rules: none. The legacy screen declared no validator on the control; a blank box left
    /// the local at zero rather than at the absent-integer sentinel
    /// (<c>SiteSettings.ascx.vb:L714-L717</c>), so a blank field persisted a real zero quota.
    /// </para>
    /// <para>
    /// This is one of six host-only members. A caller who is not a super user may not change it; see
    /// the host-only migration note in this class.
    /// </para>
    /// </remarks>
    public int? PageQuota { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of user accounts the portal may contain, or <c>null</c> when
    /// no user quota applies.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy argument 13, <c>UserQuota</c>, declared <c>As Integer</c> in the replaced signature and
    /// supplied by the <c>txtUserQuota</c> text box (rendered on read at
    /// <c>SiteSettings.ascx.vb:L347</c>). The backing column is a nullable integer, so the property
    /// is nullable, and this is a count of accounts.
    /// </para>
    /// <para>
    /// The legacy local behind this argument is the clearest artefact of the disabled compiler
    /// strictness on the whole screen: <c>SiteSettings.ascx.vb:L719</c> declares it
    /// double-precision despite its integer-suggesting name, L721 assigns it from an integer parse,
    /// and L778 passes it to an argument declared <c>As Integer</c> -- an implicit floating-point to
    /// integer narrowing that C# rejects outright. The modern contract is an integer throughout, so
    /// the conversion is explicit and a fractional value fails to bind instead of being truncated.
    /// This argument is also one of the four the legacy documentation header omitted.
    /// </para>
    /// <para>
    /// Measured rules: none. A blank box left the local at zero rather than at the absent-integer
    /// sentinel, so a blank field persisted a real zero quota.
    /// </para>
    /// <para>
    /// This is one of six host-only members. A caller who is not a super user may not change it; see
    /// the host-only migration note in this class.
    /// </para>
    /// </remarks>
    public int? UserQuota { get; set; }

    /// <summary>
    /// Gets or sets the name of the payment processor used to bill portal subscriptions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy argument 14, <c>PaymentProcessor</c>, typed <c>String</c>, supplied by the
    /// <c>cboProcessor</c> list. The legacy call site built it with a conditional-expression function
    /// whose result is an untyped object, on which a late-bound string conversion was then invoked
    /// (<c>SiteSettings.ascx.vb:L777</c>) -- another artefact of the disabled compiler strictness,
    /// substituting an empty string when no processor was selected. Both arms of that expression are
    /// free of side effects, so a short-circuiting conditional is an exact equivalent; the conversion
    /// is explicit in the modern contract because the property is typed.
    /// </para>
    /// <para>
    /// Measured rules: none. The legacy screen declared no validator on the control. An empty string
    /// may arrive where a modern reader would expect <c>null</c>, and in this case the legacy screen
    /// sent an empty string deliberately.
    /// </para>
    /// </remarks>
    public string? PaymentProcessor { get; set; }

    /// <summary>
    /// Gets or sets the account identifier presented to the payment processor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy argument 15, <c>ProcessorUserId</c>, typed <c>String</c>, supplied by the
    /// <c>txtUserId</c> text box and labelled "Processor UserId:" by the resource file. It precedes
    /// the processor credential in the replaced signature -- an ordering that is easy to reverse,
    /// because the legacy entity declares the two members the other way round
    /// (<c>PortalInfo.vb</c> declares the credential at L261 and this identifier at L269). The
    /// signature order is authoritative for this request and is the order used below.
    /// </para>
    /// <para>
    /// This is an identifier rather than a secret, so unlike <see cref="ProcessorPassword"/> it may be
    /// returned by a response contract. Measured rules: none -- the legacy screen declared no
    /// validator on the control. An empty string may arrive where a modern reader would expect
    /// <c>null</c>.
    /// </para>
    /// </remarks>
    public string? ProcessorUserId { get; set; }

    // MIGRATION: the payment-processor credential is accepted inbound but is NEVER echoed back, and
    //   that is a deliberate refusal to reproduce two measured legacy exposures.
    //
    //     PortalInfo.vb:L261 decorates this member with an XML element attribute, so the legacy
    //       system wrote the plain credential into portal template exports.
    //     SiteSettings.ascx.vb:L756-L758 assigned the submitted credential back into the rendered
    //       HTML value attribute of the text box, so it was also served back to the browser in the
    //       page markup.
    //
    //   Neither is carried forward. No response contract in this migration declares this member, so
    //   the credential travels in one direction only, and the structured-logging requirement forbids
    //   recording it: it must never appear in a log event, a diagnostic message or a problem detail.
    //   This is not an opportunistic fix of the legacy exporter -- that code is annotated and left
    //   alone -- it is a decision not to reproduce a credential leak in a NEW contract.
    //
    //   Null-versus-blank semantics, stated honestly. The replaced signature is a Sub with no
    //   optional parameters, and the legacy code-behind passed the text box contents directly
    //   (SiteSettings.ascx.vb:L778), so a blank or omitted value OVERWROTE the stored credential with
    //   an empty string -- it CLEARED it. If PortalService instead adopts "null means leave
    //   unchanged", that is a behavioural divergence from the legacy path and requires its own
    //   migration note IN THE SERVICE. This property implements neither policy and remains inert;
    //   the service is the single place the semantics are decided.

    /// <summary>
    /// Gets or sets the credential presented to the payment processor. Write-only: it is accepted
    /// here and is never returned by any response contract, and it must never be logged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy argument 16, <c>ProcessorPassword</c>, typed <c>String</c>, supplied by the
    /// <c>txtPassword</c> text box and labelled "Processor Password:" by the resource file. It is
    /// argument sixteen of the replaced signature and therefore belongs to this request under the
    /// rule that the member set is preserved exactly; it follows <see cref="ProcessorUserId"/>, not
    /// the reverse.
    /// </para>
    /// <para>
    /// See the migration note above for the two legacy exposures that are deliberately not reproduced
    /// and for the null-versus-blank semantics, which the service decides and documents. No attribute
    /// of any kind is declared on this member -- the transport, redaction and logging policies are
    /// configured at the API layer.
    /// </para>
    /// <para>
    /// Measured rules: none. The legacy screen declared no validator on the control and imposed no
    /// complexity, length or confirmation requirement, because this is a credential the portal
    /// presents to a third party rather than one it verifies.
    /// </para>
    /// </remarks>
    public string? ProcessorPassword { get; set; }

    /// <summary>
    /// Gets or sets the descriptive summary of the portal, used as page metadata.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy argument 17, <c>Description</c>, typed <c>String</c>, supplied by the
    /// <c>txtDescription</c> text box. The column was added to the portals table by a later script in
    /// the chain rather than by the baseline table, which is one of the reasons the terminal schema
    /// must be read from the whole chain and not from the baseline alone.
    /// </para>
    /// <para>
    /// Measured rules: none. The legacy screen declared no validator on the control. An empty string
    /// may arrive where a modern reader would expect <c>null</c>.
    /// </para>
    /// </remarks>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the comma-separated keywords for the portal, used as page metadata.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy argument 18, <c>KeyWords</c>, typed <c>String</c>, supplied by the <c>txtKeyWords</c>
    /// text box. The interior capital letter is the legacy spelling, carried through the argument
    /// name, the entity property at <c>PortalInfo.vb:L229</c> and the column itself, and it is
    /// preserved here deliberately rather than normalised, so that the wire contract and the column
    /// continue to agree.
    /// </para>
    /// <para>
    /// Measured rules: none. The legacy screen declared no validator on the control. An empty string
    /// may arrive where a modern reader would expect <c>null</c>.
    /// </para>
    /// </remarks>
    public string? KeyWords { get; set; }

    // MIGRATION: the background-image reference carries the SAME read-versus-write asymmetry as the
    //   logo, with an identical rewriting clause in the terminal read view at
    //   04.05.00.SqlDataProvider:L1559 --
    //
    //     CASE WHEN LEFT(LOWER(BackgroundFile), 6) = 'fileid'
    //          THEN (SELECT Folder + FileName FROM ...Files
    //                WHERE 'fileid=' + convert(varchar, ...Files.FileID) = BackgroundFile)
    //          ELSE BackgroundFile END AS BackgroundFile
    //
    //   Because reads go through that view (the GetPortal procedure at 04.04.00.SqlDataProvider:L199
    //   selects from it), the STORED value may be a "fileid=NNN" token while the value the API
    //   RETURNS is the resolved folder-and-filename path. What this property accepts is written to the
    //   column verbatim, so echoing a read value back into this request replaces the token with a
    //   resolved path and PERMANENTLY BREAKS the managed file link. Callers must send either a
    //   "fileid=NNN" token or a path deliberately, never a blind echo.
    //
    //   As with the logo, no token detection, resolution or preservation is implemented here: the
    //   file-system subtree that owns the managed-file abstraction is excluded from this migration.

    /// <summary>
    /// Gets or sets the reference to the portal background image.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy argument 19, <c>BackgroundFile</c>, typed <c>String</c>. It was supplied by the
    /// <c>ctlBackground</c> picker control declared at <c>sitesettings.ascx:L150</c>, whose selected
    /// value the code-behind read into a local (<c>SiteSettings.ascx.vb:L699</c>) before passing it
    /// as the nineteenth argument at L779.
    /// </para>
    /// <para>
    /// The value is either a managed-file token of the form <c>fileid=NNN</c> or a path. See the
    /// migration note immediately above: the read and write representations differ, and echoing a
    /// read value back here breaks the file link.
    /// </para>
    /// <para>
    /// Measured rules: none. The legacy screen declared no validator on the control. An empty string
    /// and <c>null</c> are indistinguishable in legacy data.
    /// </para>
    /// </remarks>
    public string? BackgroundFile { get; set; }

    /// <summary>
    /// Gets or sets the number of days of site-activity history retained for the portal, or
    /// <c>null</c> when no retention period is set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy argument 20, <c>SiteLogHistory</c>, typed <c>Integer</c>, supplied by the
    /// <c>txtSiteLogHistory</c> text box. The resource file is explicit about the unit: the label
    /// reads "Site Log History (Days):" and the help text "The number of days of site activity that
    /// is kept for this site." The backing column is a nullable integer, so the property is nullable.
    /// </para>
    /// <para>
    /// This member is the one place on the legacy save path where a blank field produced the
    /// absent-integer sentinel rather than zero: the local is initialised to the value -1 as a bare
    /// literal, not through the null contract, and is overwritten only when the box is non-empty
    /// (<c>SiteSettings.ascx.vb:L724-L727</c>). The data layer then converted that sentinel to a
    /// database null. A caller must express "no retention period" as <c>null</c> and must not send
    /// -1 expecting it to be interpreted as absent.
    /// </para>
    /// <para>
    /// Measured rules: none. The legacy screen declared no validator on the control.
    /// </para>
    /// <para>
    /// This is one of six host-only members. A caller who is not a super user may not change it; see
    /// the host-only migration note in this class.
    /// </para>
    /// </remarks>
    public int? SiteLogHistory { get; set; }

    // MIGRATION: the four page references that follow -- arguments 21 to 24 -- all used the legacy
    //   absent-integer sentinel, whose value is -1, to mean "no page assigned". Each local was
    //   initialised to that sentinel and overwritten only when the corresponding list had a selected
    //   item (SiteSettings.ascx.vb:L734, L739, L744 and L749), and the data layer then converted the
    //   sentinel to a database null on the way to the nullable column
    //   (Library/Components/Shared/Null.vb:L167-L169). Null is the modern equivalent, and a caller
    //   must NOT send -1 expecting it to be interpreted as "clear the page reference": that value is
    //   simply an identifier this contract passes through unchanged.
    //
    //   Nor may any of these be tested for absence by value, in either direction. Tabs.TabID is
    //   declared IDENTITY (0, 1) at 01.00.00.SqlDataProvider:L140, so ZERO IS A LEGITIMATE PAGE
    //   IDENTIFIER and must never be read as "no page". The legacy absence test compounded the
    //   hazard by reporting the sentinel value as absent for ANY integer
    //   (Library/Components/Shared/Null.vb:L210-L211), including a genuine identifier that happened
    //   to hold it. Presence is expressed by nullability alone, and this type declares no absence
    //   helper of any kind.

    /// <summary>
    /// Gets or sets the identifier of the page shown as the portal splash screen, or <c>null</c> when
    /// the portal has no splash page.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy argument 21, <c>SplashTabId</c>, typed <c>Integer</c>, supplied by the
    /// <c>cboSplashTabId</c> list, which the legacy screen populated from the portal's page tree
    /// (<c>SiteSettings.ascx.vb:L299-L300</c>). The backing column is a nullable integer, so the
    /// property is nullable. This argument is one of the four the legacy documentation header omitted.
    /// </para>
    /// <para>
    /// See the migration note above: the legacy unset value was the absent-integer sentinel, zero is a
    /// legitimate page identifier, and <c>null</c> is the modern expression of "no splash page".
    /// Measured rules: none -- the legacy screen declared no validator on the control.
    /// </para>
    /// </remarks>
    public int? SplashTabId { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the portal home page, or <c>null</c> when the portal has no
    /// explicit home page.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy argument 22, <c>HomeTabId</c>, typed <c>Integer</c>, supplied by the
    /// <c>cboHomeTabId</c> list. The backing column is a nullable integer, so the property is
    /// nullable.
    /// </para>
    /// <para>
    /// See the migration note above: the legacy unset value was the absent-integer sentinel, zero is a
    /// legitimate page identifier, and <c>null</c> is the modern expression of "no home page".
    /// Measured rules: none -- the legacy screen declared no validator on the control.
    /// </para>
    /// </remarks>
    public int? HomeTabId { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the portal sign-in page, or <c>null</c> when the portal has no
    /// dedicated sign-in page.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy argument 23, <c>LoginTabId</c>, typed <c>Integer</c>, supplied by the
    /// <c>cboLoginTabId</c> list. The backing column is a nullable integer, so the property is
    /// nullable.
    /// </para>
    /// <para>
    /// See the migration note above: the legacy unset value was the absent-integer sentinel, zero is a
    /// legitimate page identifier, and <c>null</c> is the modern expression of "no sign-in page".
    /// Measured rules: none -- the legacy screen declared no validator on the control.
    /// </para>
    /// </remarks>
    public int? LoginTabId { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the portal user-account page, or <c>null</c> when the portal has
    /// no dedicated user-account page.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy argument 24, <c>UserTabId</c>, typed <c>Integer</c>, supplied by the
    /// <c>cboUserTabId</c> list. The backing column is a nullable integer, so the property is
    /// nullable.
    /// </para>
    /// <para>
    /// See the migration note above: the legacy unset value was the absent-integer sentinel, zero is a
    /// legitimate page identifier, and <c>null</c> is the modern expression of "no user-account page".
    /// Measured rules: none -- the legacy screen declared no validator on the control.
    /// </para>
    /// </remarks>
    public int? UserTabId { get; set; }

    /// <summary>
    /// Gets or sets the default culture code for the portal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy argument 25, <c>DefaultLanguage</c>, typed <c>String</c>, supplied by the
    /// <c>cboDefaultLanguage</c> list and passed as its selected value
    /// (<c>SiteSettings.ascx.vb:L780</c>).
    /// </para>
    /// <para>
    /// The value is carried as a plain culture code. The legacy resource-file mechanism that consumed
    /// it is not ported -- user-facing wording is authored directly in the client templates, with the
    /// legacy resource files read only as the authoritative source of that wording -- so this property
    /// records a portal preference and drives no translation runtime.
    /// </para>
    /// <para>
    /// Measured rules: none. The legacy screen declared no validator on the control, because a
    /// drop-down list cannot submit a value it does not offer. An empty string may arrive where a
    /// modern reader would expect <c>null</c>.
    /// </para>
    /// </remarks>
    public string? DefaultLanguage { get; set; }

    // MIGRATION: the column name is spelled differently in the schema and in the code, and the modern
    //   C# spelling is chosen deliberately. The terminal read view projects this column as
    //   "TimezoneOffset" with a lower-case z (measured in the view definition in
    //   04.05.00.SqlDataProvider), whereas the legacy entity property at PortalInfo.vb:L372 and
    //   argument 26 of the replaced signature both spell it "TimeZoneOffset" with a capital Z. SQL
    //   identifiers are case-insensitive, so the legacy code worked against either spelling and the
    //   discrepancy was invisible.
    //
    //   This request uses the capital-Z spelling, which is both the correct C# compound-word casing
    //   and the spelling the replaced signature used. The view's lower-case spelling is recorded here
    //   because the Infrastructure entity configuration binds the column by name and must use the
    //   name the schema actually carries; a mapping written from this property name alone would be
    //   wrong in a case-sensitive tool.

    /// <summary>
    /// Gets or sets the portal's offset from co-ordinated universal time, in minutes, or <c>null</c>
    /// when no portal offset is set.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy argument 26, <c>TimeZoneOffset</c>, typed <c>Integer</c>, supplied by the
    /// <c>cboTimeZone</c> list through an explicit integer conversion of its selected value
    /// (<c>SiteSettings.ascx.vb:L780</c>) and populated on read from the stored offset (L425). The
    /// resource file labels it "Portal TimeZone:". The backing column is a nullable integer, so the
    /// property is nullable.
    /// </para>
    /// <para>
    /// The unit is minutes, measured rather than assumed: the offsets the legacy list offered are
    /// enumerated in <c>Website/App_GlobalResources/TimeZones.xml</c>, where the entry for twelve
    /// hours behind universal time carries the key -720, the entry for five hours behind carries -300,
    /// the entry for the United Kingdom carries 0, the entry for one hour ahead carries 60, and the
    /// entry for Tehran carries 210 -- a half-hour offset that only a minute-based unit can express.
    /// The measured range of the supplied list is -720 through 780.
    /// </para>
    /// <para>
    /// Zero is a real, legitimate offset -- it is the United Kingdom -- and must never be treated as
    /// meaning absent. Absence is expressed only by <c>null</c>.
    /// </para>
    /// <para>
    /// Measured rules: none. The legacy screen declared no validator on the control. A validator may
    /// reasonably confirm the value falls within the offered range, but that rule is not one the
    /// legacy screen enforced and it must be recorded as an addition if adopted.
    /// </para>
    /// </remarks>
    public int? TimeZoneOffset { get; set; }

    /// <summary>
    /// Gets or sets the portal home directory, as a path relative to the application root.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy argument 27 and the last member of the replaced signature, <c>HomeDirectory</c>, typed
    /// <c>String</c>, supplied by the <c>txtHomeDirectory</c> text box and labelled "Home Directory:"
    /// by the resource file, whose help text reads "Enter the Home Directory for this site". This
    /// argument is the fourth of those the legacy documentation header omitted.
    /// </para>
    /// <para>
    /// The value is RELATIVE. The absolute server path was never submitted and is never accepted: the
    /// legacy entity derived it on demand in the read-only <c>HomeDirectoryMapPath</c> property at
    /// <c>PortalInfo.vb:L388</c>, reaching into two subsystems this migration excludes, and accepting
    /// a server filesystem path from an HTTP client would be a path-traversal hazard. The absolute
    /// path is derived server-side from this relative value and from the hosting environment.
    /// </para>
    /// <para>
    /// Measured rules: none. The legacy screen declared no validator on the control -- notably no
    /// pattern rule constraining the path -- so path-shape and traversal rules imposed by the
    /// validator are additions rather than reproductions and must be recorded as such. An empty string
    /// may arrive where a modern reader would expect <c>null</c>.
    /// </para>
    /// </remarks>
    public string? HomeDirectory { get; set; }
}
