using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Dtos.Portal;

/// <summary>
/// Wire contract describing a single portal in full: the tenant container that owns a site's
/// pages, users, roles and modules.
/// </summary>
/// <remarks>
/// <para>
/// This is the response body of <c>GET /api/v1/portals/{id}</c>, served by
/// <c>PortalsController</c>, and it is also the payload the Application-layer portal service
/// reports back when a portal has been created or updated. It is the widest of the portal
/// contracts and the one a client-side portal model mirrors most closely, so every member below
/// is named exactly as a consumer will see it on the wire.
/// </para>
/// <para>
/// Ported from the legacy <c>PortalInfo</c> class at
/// <c>Library/Components/Portal/PortalInfo.vb</c>, which declares exactly thirty-nine public
/// properties between lines 85 and 395. Thirty-six of those thirty-nine appear here. The three
/// that do not are named, with the measured reason for each, in the comments that open the class
/// body. A thirty-seventh member, <see cref="Aliases"/>, has no legacy counterpart on that class
/// and is described on the member itself.
/// </para>
/// <para>
/// The type is an inert data carrier. It holds values and exposes no behaviour: no validation,
/// no clamping, no normalisation, no computed member, no constructor logic and no access to any
/// data store. Every legacy behaviour that used to surround these values is implemented
/// elsewhere by design. Reading and writing the persisted record belongs to the repository
/// abstractions behind the Application layer; translating between the persisted record and this
/// contract belongs to <c>Application/Mapping/PortalMappings.cs</c>; the rules that decide which
/// values an administrator may change belong to <c>Application/Services/PortalService.cs</c> and
/// to the client's portal-settings form.
/// </para>
/// <para>
/// Read-only members. Five of the members below cannot be written back, because the legacy read
/// path does not read the <c>Portals</c> table directly. Both readers select through a database
/// view: <c>GetPortal</c> issues <c>SELECT * FROM ...vw_Portals WHERE PortalId = @PortalId</c>
/// (<c>Website/Providers/DataProviders/SqlDataProvider/04.04.00.SqlDataProvider</c> lines 199
/// and 205 to 206) and <c>GetPortals</c> issues
/// <c>SELECT * FROM ...vw_Portals ORDER BY PortalName</c> (line 234). The view's terminal
/// definition spans lines 1530 to 1587 of <c>04.05.00.SqlDataProvider</c>, and it computes five
/// of its columns rather than passing them through. Each affected member says so, and no member
/// of this contract may be assumed writable merely because it is settable here.
/// </para>
/// <para>
/// No persisted record type and no strongly typed identifier wrapper is exposed here. Keeping
/// the transported shape distinct from the stored one is what allows the legacy absent-value
/// conventions noted throughout to be honoured at the API edge without contaminating the model
/// behind it, and keeping the identifiers as plain numbers and plain text is what allows a
/// client model to declare the same fields without a translation table.
/// </para>
/// <para>
/// Absent values, and why no single value may be read as "missing". The legacy codebase encoded
/// an absent value inside the value's own range rather than alongside it: the shared helper at
/// <c>Library/Components/Shared/Null.vb</c> lines 36 to 85 answers a negative number for an
/// absent whole number, the empty string for absent text, the lowest representable calendar date
/// for an absent date and the all-zero identifier for an absent identifier, and its companion
/// converter at line 88 applied those substitutions on every database read. Two of those
/// encodings collide with real data on this very type. The <c>Portals</c> identity column is
/// seeded with a negative number
/// (<c>Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider</c> line 77), so
/// the encoded "absent whole number" is simultaneously a valid portal identifier; and the
/// installation ships a portal whose identifier is zero, whose administrator role identifier is
/// also zero (line 7125), while the <c>Roles</c> and <c>Tabs</c> identity columns are both seeded
/// at zero as well (lines 115 and 140). Consumers must therefore never infer absence from a
/// negative number, from zero, or from an empty string. Where the legacy encoding is observable
/// on a member, that member records it, and the choice of whether a mapper forwards the legacy
/// encoding or substitutes no value at all is recorded in the mapper rather than resolved here.
/// </para>
/// </remarks>
public sealed class PortalDetailDto
{
    // MIGRATION: every XML serialisation attribute on the legacy class is dropped. The legacy
    // type was declared a serialisation root - <XmlRoot("settings", IsNullable:=False)> at
    // Library/Components/Portal/PortalInfo.vb line 29, over the import at line 26 - and
    // thirty-seven of its thirty-nine properties carried an <XmlElement("...")> name while two
    // carried <XmlIgnore()>. That decoration existed to drive the legacy portal-template export
    // and import, which wrote a portal to a document whose element names were the lower-case
    // strings in those attributes. This contract is serialised as JSON by the API layer using
    // the member names declared below, so the legacy element names are not reproduced anywhere
    // and no serialisation attribute appears in this file. A consumer that expects the legacy
    // lower-case element names will not find them.

    // MIGRATION: member names are modernised from the legacy all-capitals acronym spelling to
    // the Pascal-cased .NET form, so two names on this contract differ from their legacy
    // counterparts and from the underlying column names:
    //
    //     PortalID (PortalInfo.vb line 85)  becomes PortalId
    //     GUID     (PortalInfo.vb line 245) becomes Guid
    //
    // These are real, externally observable contract changes, and they are applied identically
    // across every portal contract in this folder so that a client model can reuse these
    // identifiers verbatim. Stored column names are untouched; the entity configuration in the
    // infrastructure layer continues to bind the legacy spellings.

    // MIGRATION: three of the thirty-nine legacy properties are deliberately absent from this
    // contract. Each exclusion is measured, not assumed, and each is recorded here so that it is
    // not reversed by inspection of the legacy class alone.
    //
    // 1. ProcessorPassword (PortalInfo.vb line 261) - the password half of the portal's
    //    payment-gateway credential. It is omitted from this and from every other response
    //    contract. The legacy property carried <XmlElement("processorpassword")>, so the
    //    template exporter wrote the live gateway credential into a portal document; and the
    //    read path returns it too, because the view projects the column at
    //    04.05.00.SqlDataProvider line 1572. Its availability on the read path is precisely why
    //    its absence here is an active decision rather than a side effect of the query. Omitting
    //    the member is the structural guarantee that the credential cannot reach a response
    //    body, a client store or a log line, and it costs nothing: the two harmless halves of
    //    the gateway configuration, PaymentProcessor (line 253) and ProcessorUserId (line 269),
    //    are both present below. An update request may still accept a replacement credential
    //    inbound; a credential is write-only, never echoed. The legacy exporter itself is left
    //    exactly as it is - this is a new contract declining to reproduce a disclosure, not a
    //    repair of the old one.
    //
    // 2. HomeDirectoryMapPath (PortalInfo.vb line 388) - the only property on the legacy class
    //    declared read-only, and the only one with no backing field. Its body, at lines 390 to
    //    391, composes an absolute server filesystem path by asking a file-system component to
    //    map a path built from the excluded shared-globals module's application path. Both of
    //    those subsystems are outside the boundary of this migration, the value is absent from
    //    the view that the read path selects, and publishing an absolute server path on an
    //    HTTP API would disclose deployment layout to any caller. It appears on no contract.
    //    The portal-relative HomeDirectory it is derived from is present below, which is the
    //    part a client actually needs.
    //
    // 3. Version (PortalInfo.vb line 395) - present on the legacy class but not on the portal
    //    table. The sole nvarchar(8) column of that name in the entire eighty-eight-script
    //    upgrade chain is at 02.00.00.SqlDataProvider line 5144, the fourth column of
    //    CREATE TABLE ...DesktopModules, which opens at line 5140; every other occurrence in
    //    the chain is a stored-procedure parameter of the desktop-module procedures. It is also
    //    absent from the view that the read path selects, so the legacy property could only ever
    //    have reported whatever a caller had assigned in memory. It is excluded so that nobody
    //    reintroduces it as a portal attribute.

    // MIGRATION: the schema type is authoritative wherever the legacy sources disagree about a
    // value's type, because the existing database is not altered by this migration. Two members
    // below are affected, and both disagreements are three-way. The measured resolution is
    // recorded on each member. The baseline CREATE TABLE for the portal table
    // (01.00.00.SqlDataProvider lines 76 to 93) is NOT the terminal schema and must not be read
    // as though it were: later scripts re-type columns in place, and the baseline additionally
    // declares an upload-directory column at line 79 and a gateway-account column at line 81,
    // neither of which is among the thirty-nine legacy properties at all.

    /// <summary>
    /// Gets or sets the identifier of the portal this contract describes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Corresponds to the legacy <c>PortalID</c> property
    /// (<c>Library/Components/Portal/PortalInfo.vb</c> line 85) and to the
    /// <c>Portals.PortalID</c> column, declared <c>[int] IDENTITY (-1, 1) NOT NULL</c> at
    /// <c>Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider</c> line 77.
    /// The column is not nullable, so this member is a plain whole number. The database assigns
    /// the value on insert, and on a response it is always populated.
    /// </para>
    /// <para>
    /// Every value this member can hold is a meaningful portal identifier, including negative
    /// ones and zero. Because the identity column is seeded at <c>-1</c>, the first portal a
    /// database creates is numbered <c>-1</c> and the second is numbered zero, and the stock
    /// installation ships its <c>_default</c> portal at zero (line 7125). The same <c>-1</c> is
    /// what the legacy shared helper answered to mean "no whole number"
    /// (<c>Library/Components/Shared/Null.vb</c> lines 41 to 45), and its absence test reported
    /// that encoding as absent for any whole number at all (lines 210 to 211), host portals
    /// included. Consumers must not treat any particular value of this member as meaning that no
    /// portal was identified.
    /// </para>
    /// </remarks>
    public int PortalId { get; set; }

    /// <summary>
    /// Gets or sets the display name of the portal.
    /// </summary>
    /// <remarks>
    /// Corresponds to the legacy <c>PortalName</c> property
    /// (<c>Library/Components/Portal/PortalInfo.vb</c> line 93) and to the
    /// <c>Portals.PortalName</c> column, declared <c>[nvarchar] (128) NOT NULL</c> at
    /// <c>Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider</c> line 73.
    /// The legacy administration screen presented it under the label "Portal Name:" and required
    /// a value. This member is nevertheless a nullable string, because a response contract
    /// transports whatever the record holds rather than asserting a rule; the rule itself is
    /// declared once, in the request validators under <c>Application/Validation/</c>. An empty
    /// value and no value at all are not distinguishable in legacy data, for the reason given on
    /// the type.
    /// </remarks>
    public string? PortalName { get; set; }

    /// <summary>
    /// Gets or sets the portal's description, used as page metadata.
    /// </summary>
    /// <remarks>
    /// Corresponds to the legacy <c>Description</c> property
    /// (<c>Library/Components/Portal/PortalInfo.vb</c> line 221). The legacy administration
    /// screen labelled it "Description:" and treated it as free text with no length rule of its
    /// own. An empty value may legitimately appear where a modern consumer would expect none.
    /// </remarks>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the portal's keywords, used as page metadata.
    /// </summary>
    /// <remarks>
    /// Corresponds to the legacy <c>KeyWords</c> property
    /// (<c>Library/Components/Portal/PortalInfo.vb</c> line 229). The capital <c>W</c> is the
    /// legacy spelling and is preserved deliberately, so that this contract, the stored column
    /// and the client model all agree on one identifier. The legacy administration screen
    /// labelled the field "Keywords:" and expected a comma-separated list, which this contract
    /// transports as a single string exactly as stored, applying no splitting rule of its own.
    /// </remarks>
    public string? KeyWords { get; set; }

    /// <summary>
    /// Gets or sets the text rendered in the portal's page footer, typically a copyright notice.
    /// </summary>
    /// <remarks>
    /// Corresponds to the legacy <c>FooterText</c> property
    /// (<c>Library/Components/Portal/PortalInfo.vb</c> line 109) and to the
    /// <c>Portals.FooterText</c> column, declared <c>[nvarchar] (100) NULL</c> at
    /// <c>Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider</c> line 75.
    /// The column admits no value, so a nullable string is the faithful representation of the
    /// measured schema rather than a convenience.
    /// </remarks>
    public string? FooterText { get; set; }

    // MIGRATION: LogoFile and BackgroundFile are asymmetric between reading and writing, and a
    // naive round trip through them destroys data. The stored column may hold either a plain
    // relative path or a token of the form "fileid=NNN" that points at a row in the managed file
    // table. The view the read path selects through resolves that token before returning it: at
    // 04.05.00.SqlDataProvider lines 1535 to 1544 it evaluates
    //
    //     CASE WHEN LEFT(LOWER(LogoFile), 6) = 'fileid'
    //          THEN (SELECT Folder + FileName FROM ...Files
    //                WHERE 'fileid=' + convert(varchar, ...Files.FileID) = LogoFile)
    //          ELSE LogoFile END AS LogoFile
    //
    // and it repeats the identical CASE for BackgroundFile at lines 1559 to 1568. What this
    // contract therefore reports is the RESOLVED folder-and-filename path whenever the stored
    // value was a token, and the stored value itself otherwise - the two cases are
    // indistinguishable once received. Echoing a value read here straight back into a portal
    // update request overwrites the token with a path and permanently severs the link to the
    // managed file row. A caller that means to leave the image unchanged must omit the field
    // rather than resend it. Token resolution is deliberately not implemented here: the managed
    // file subsystem is outside the boundary of this migration, and a dedicated file-reference
    // contract was considered and rejected because the asymmetry is fully expressible as text
    // plus this note.

    /// <summary>
    /// Gets or sets the portal's logo image, as resolved by the read path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Corresponds to the legacy <c>LogoFile</c> property
    /// (<c>Library/Components/Portal/PortalInfo.vb</c> line 101) and to the
    /// <c>Portals.LogoFile</c> column, declared <c>[nvarchar] (50) NULL</c> at
    /// <c>Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider</c> line 74.
    /// </para>
    /// <para>
    /// The value is transformed on read and must not be written back unexamined. See the note
    /// above this member for the measured rewrite and the data-loss hazard it creates.
    /// </para>
    /// </remarks>
    public string? LogoFile { get; set; }

    /// <summary>
    /// Gets or sets the portal's background image, as resolved by the read path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Corresponds to the legacy <c>BackgroundFile</c> property
    /// (<c>Library/Components/Portal/PortalInfo.vb</c> line 237).
    /// </para>
    /// <para>
    /// The value is transformed on read and must not be written back unexamined, on exactly the
    /// same terms as <see cref="LogoFile"/>. See the note above that member.
    /// </para>
    /// </remarks>
    public string? BackgroundFile { get; set; }

    // MIGRATION: the legacy expiry date has no absent state of its own. The legacy property was
    // typed as a value that always holds some date (PortalInfo.vb line 117), so a database null
    // became the lowest representable calendar date on read, through the substitution the shared
    // helper performed at Library/Components/Shared/Null.vb lines 66 to 70 and line 88. Its
    // companion absence test compared only the DATE part - it converted the value and then
    // compared it against the date component of that lowest date, at lines 222 to 223 - so any
    // instant falling on the first day of year one counted as absent regardless of its time of
    // day. This member is a nullable date so that a genuinely unset expiry can be transported as
    // no value at all, but the legacy encoding is NOT silently rewritten here: a record holding
    // the lowest representable date is transported as that date. Whether a mapper forwards it or
    // substitutes no value is decided, and documented, in the mapper.

    /// <summary>
    /// Gets or sets the date on which the portal's hosting arrangement expires.
    /// </summary>
    /// <remarks>
    /// Corresponds to the legacy <c>ExpiryDate</c> property
    /// (<c>Library/Components/Portal/PortalInfo.vb</c> line 117) and to the
    /// <c>Portals.ExpiryDate</c> column, declared <c>[datetime] NULL</c> at
    /// <c>Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider</c> line 76.
    /// The stock <c>_default</c> portal ships with no expiry (line 7125). The legacy
    /// administration screen described the field as the date the hosting contract for the portal
    /// expires. See the note above this member for the legacy encoding of an unset expiry.
    /// </remarks>
    public DateTime? ExpiryDate { get; set; }

    /// <summary>
    /// Gets or sets the way the portal admits new user accounts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Corresponds to the legacy <c>UserRegistration</c> property, which exposed the raw stored
    /// discriminator as a bare whole number
    /// (<c>Library/Components/Portal/PortalInfo.vb</c> line 125), and to the
    /// <c>Portals.UserRegistration</c> column, whose terminal shape is a non-nullable whole
    /// number defaulting to zero. Because the column admits no absent state, this member is a
    /// non-nullable enumeration rather than a nullable one, and zero is a genuine chosen mode
    /// rather than a stand-in for a missing value.
    /// </para>
    /// <para>
    /// The named members and their ordinals are recorded on
    /// <see cref="UserRegistrationMode"/>. They were recovered from the administration option
    /// list at <c>Website/admin/Portal/sitesettings.ascx</c> lines 230 to 233, whose four items
    /// carry the values zero through three under the labels None, Private, Public and Verified,
    /// and the legacy screen bound the stored number straight to that list's zero-based selected
    /// position (<c>Website/admin/Portal/SiteSettings.ascx.vb</c> line 277). The stock
    /// <c>_default</c> portal ships with the public mode
    /// (<c>Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider</c> line
    /// 7125).
    /// </para>
    /// <para>
    /// The enumeration is transported as its numeric value, which is byte-for-byte what the
    /// column stores and what the legacy screen posted, so naming the mode does not change the
    /// shape of the payload. No serialisation attribute is applied here to alter that.
    /// </para>
    /// </remarks>
    public UserRegistrationMode UserRegistration { get; set; }

    /// <summary>
    /// Gets or sets the way banner advertising is administered for the portal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Corresponds to the legacy <c>BannerAdvertising</c> property, which likewise exposed the
    /// raw stored discriminator as a bare whole number
    /// (<c>Library/Components/Portal/PortalInfo.vb</c> line 133), and to the
    /// <c>Portals.BannerAdvertising</c> column, whose terminal shape is a non-nullable whole
    /// number defaulting to zero. This member is therefore a non-nullable enumeration, and its
    /// zero member means advertising is switched off rather than that no mode was recorded.
    /// </para>
    /// <para>
    /// The named members and their ordinals are recorded on
    /// <see cref="BannerAdvertisingMode"/>, recovered from the administration option list at
    /// <c>Website/admin/Portal/sitesettings.ascx</c> lines 123 to 125, whose three items carry
    /// the values zero, one and two under the labels None, Site and Host. The stock
    /// <c>_default</c> portal ships with advertising switched off
    /// (<c>Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider</c> line
    /// 7125).
    /// </para>
    /// <para>
    /// One live legacy rule keys off the host member: at
    /// <c>Website/admin/Portal/SiteSettings.ascx.vb</c> line 295 the administration screen
    /// disabled portal-level banner editing whenever the stored mode was the host one, and line
    /// 296 revealed an explanatory label for the same mode, both only when the caller was not a
    /// super user. That rule is preserved in <c>Application/Services/PortalService.cs</c> and in
    /// the client's portal-settings form. It is deliberately not expressed here, because this
    /// contract reports the mode and never decides what may be done with it: no member of this
    /// type says whether a field is editable.
    /// </para>
    /// </remarks>
    public BannerAdvertisingMode BannerAdvertising { get; set; }

    /// <summary>
    /// Gets or sets the ISO currency code in which the portal's hosting charges are expressed.
    /// </summary>
    /// <remarks>
    /// Corresponds to the legacy <c>Currency</c> property
    /// (<c>Library/Components/Portal/PortalInfo.vb</c> line 149) and to the
    /// <c>Portals.Currency</c> column, declared <c>[char] (3) NULL</c> at
    /// <c>Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider</c> line 82;
    /// the stock <c>_default</c> portal ships with <c>USD</c> (line 7125). Because the column is
    /// fixed-width text, a stored code may arrive padded to three characters. This contract
    /// transports the value exactly as stored and trims nothing; the three-character rule is
    /// declared once, in the request validators under <c>Application/Validation/</c>. This is the
    /// unit in which <see cref="HostFee"/> is denominated.
    /// </remarks>
    public string? Currency { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the user who administers the portal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Corresponds to the legacy <c>AdministratorId</c> property
    /// (<c>Library/Components/Portal/PortalInfo.vb</c> line 141) and to the
    /// <c>Portals.AdministratorId</c> column, declared <c>[int] NULL</c> at
    /// <c>Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider</c> line 80,
    /// which references the user table. The column admits no value, so this member is a nullable
    /// whole number. The stock <c>_default</c> portal names user nine as its administrator (line
    /// 7125).
    /// </para>
    /// <para>
    /// This member is the join key from which <see cref="Email"/> is derived by the read path.
    /// Only the identifier is carried: no user contract is nested here, because the portal
    /// contracts compose nothing from another resource group, and a caller that needs the
    /// administrator's full record fetches it from the users resource.
    /// </para>
    /// </remarks>
    public int? AdministratorId { get; set; }

    // MIGRATION: Email is NOT a portal attribute. It is the administrator's email address, and
    // it reaches this contract only because the view the read path selects through joins the
    // user table to fetch it. The view ends, at 04.05.00.SqlDataProvider lines 1586 to 1587,
    //
    //     FROM ...Portals AS P
    //     LEFT OUTER JOIN ...Users AS U ON P.AdministratorId = U.UserID
    //
    // and it projects the column unqualified, as plain "Email", at line 1574 - which resolves at
    // all only because exactly one of the two joined tables declares such a column. That table
    // is the user table: its column is declared [Email] [nvarchar] (100) NOT NULL at
    // 01.00.00.SqlDataProvider line 107. The portal table has no email column anywhere in the
    // eighty-eight-script chain; this was confirmed twice, once by scanning every ALTER TABLE
    // against the portal table for such a column and once by scanning the body of every
    // CREATE TABLE for it, and neither scan found one. Two consequences follow, and both are
    // load-bearing: the member is READ-ONLY, which is why the legacy update entry point omits it
    // from its argument list; and because the join is an OUTER one, a portal whose administrator
    // identifier matches no user row yields no value at all, so a nullable string is required
    // rather than merely tidy.

    /// <summary>
    /// Gets or sets the email address of the portal's administrator. Read-only: derived by the
    /// read path from a join, not stored against the portal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Corresponds to the legacy <c>Email</c> property
    /// (<c>Library/Components/Portal/PortalInfo.vb</c> line 285). See the note above this member
    /// for the measured derivation and for why this value cannot be written back.
    /// </para>
    /// <para>
    /// The value is transported as plain text and is deliberately neither validated nor wrapped
    /// in a specialised address type. The legacy property carried no length rule, no
    /// presence rule and no pattern rule of any kind - the contrast with the user record's own
    /// address property, which carried all three, is stark and intentional. Real shipped data
    /// confirms the looser treatment is necessary rather than merely faithful: the installation
    /// seeds its host and administrator accounts with the bare words <c>host</c> and
    /// <c>admin</c> in this column
    /// (<c>Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider</c> lines
    /// 7205 and 7207), and the <c>_default</c> portal's administrator is the latter of the two,
    /// so the stock installation's own portal reports a value that no address pattern would
    /// accept. The legacy pattern constant at <c>Library/Components/Shared/Globals.vb</c> line
    /// 132 would reject both, and would also reject an address whose top-level domain runs past
    /// four letters. Any rule that is wanted belongs to the request validators under
    /// <c>Application/Validation/</c>, where it can be applied to a submitted value without
    /// making an existing record impossible to report.
    /// </para>
    /// </remarks>
    public string? Email { get; set; }

    // MIGRATION: HostFee and HostSpace each carry a three-way type disagreement between the
    // legacy sources, and the resolution is that the SCHEMA wins, because this migration does not
    // alter the existing database and must be able to read every stored value.
    //
    //     HostFee    legacy class: single-precision (PortalInfo.vb line 157)
    //                legacy update entry point: double-precision
    //                (PortalController.vb line 1568, argument ten)
    //                terminal schema: money, not nullable, defaulting to zero
    //                ALTER TABLE ...Portals ALTER COLUMN [HostFee] [money] NOT NULL
    //                (03.01.01.SqlDataProvider line 1118, default added at line 1129)
    //                => reported here as a nullable decimal.
    //
    //     HostSpace  legacy class: whole number (PortalInfo.vb line 165)
    //                legacy update entry point: double-precision
    //                (PortalController.vb line 1568, argument eleven)
    //                terminal schema: int, not nullable, defaulting to zero
    //                ALTER TABLE ...Portals ALTER COLUMN [HostSpace] [int] NOT NULL
    //                (03.01.01.SqlDataProvider line 1119, default added at line 1131)
    //                => reported here as a nullable whole number.
    //
    // The asymmetry between the two is measured, not inferred, and three independent readings
    // agree on it. The stored-procedure parameters for the fee are declared money throughout
    // while every parameter for the quota is declared int and never money. The table-rebuild
    // script converts only the fee, copying the quota unchanged: it issues
    // "SELECT ... CONVERT(money, HostFee), HostSpace ... FROM Portals"
    // at 01.00.05.SqlDataProvider line 1412. And the administration screen's own wording says
    // the same thing - the fee is described as a monthly charge and rendered through a
    // two-decimal money format (SiteSettings.ascx.vb lines 143 to 149), while the quota is
    // described as an amount of disk space in megabytes.
    //
    // Neither member is widened to double-precision merely because the legacy update entry point
    // was, and neither is narrowed to single-precision merely because the legacy class was. Both
    // are nullable because a portal may legitimately have no hosting arrangement recorded, and
    // neither setter clamps, rounds or floors anything: the legacy codebase does clamp two fees
    // to zero using a conditional helper at PortalController.vb lines 395 and 398, but those two
    // lines guard a ROLE's service and trial fees inside a role-creation helper, not the portal
    // hosting fee, and in either case clamping is service logic that has no place in a data
    // carrier.

    /// <summary>
    /// Gets or sets the recurring monetary fee charged for hosting the portal, denominated in
    /// <see cref="Currency"/>.
    /// </summary>
    /// <remarks>
    /// Corresponds to the legacy <c>HostFee</c> property
    /// (<c>Library/Components/Portal/PortalInfo.vb</c> line 157) and to the
    /// <c>Portals.HostFee</c> column, whose terminal type is <c>money</c>. The legacy
    /// administration screen labelled it "Hosting Fee:" and described it as the monthly charge
    /// for hosting the site. A decimal is used rather than a binary floating-point type so that
    /// a monetary amount survives transport without representation error. See the note above this
    /// member for the full type derivation.
    /// </remarks>
    public decimal? HostFee { get; set; }

    /// <summary>
    /// Gets or sets the disk-space quota allowed to the portal, in megabytes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Corresponds to the legacy <c>HostSpace</c> property
    /// (<c>Library/Components/Portal/PortalInfo.vb</c> line 165) and to the
    /// <c>Portals.HostSpace</c> column, whose terminal type is <c>int</c>. A whole number is
    /// therefore correct: the value counts megabytes and has no fractional part. See the note
    /// above this member for the full type derivation.
    /// </para>
    /// <para>
    /// Zero carries meaning here and must not be read as an unrecorded quota. The legacy
    /// administration screen labelled the field "Disk Space:" and instructed the administrator to
    /// enter zero for unlimited space, so a stored zero states that the portal is uncapped. The
    /// stock <c>_default</c> portal ships with a small finite quota
    /// (<c>Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider</c> line
    /// 7125).
    /// </para>
    /// </remarks>
    public int? HostSpace { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of pages the portal is permitted to contain.
    /// </summary>
    /// <remarks>
    /// Corresponds to the legacy <c>PageQuota</c> property
    /// (<c>Library/Components/Portal/PortalInfo.vb</c> line 173), presented by the legacy
    /// administration screen as "Page Quota:". It is a limit, not a measurement: the number of
    /// pages the portal actually holds is reported separately by <see cref="Pages"/>. The member
    /// is a nullable whole number because a portal need not have a page limit recorded.
    /// </remarks>
    public int? PageQuota { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of user accounts the portal is permitted to contain.
    /// </summary>
    /// <remarks>
    /// Corresponds to the legacy <c>UserQuota</c> property
    /// (<c>Library/Components/Portal/PortalInfo.vb</c> line 181), presented by the legacy
    /// administration screen as "User Quota:". As with <see cref="PageQuota"/> this is a limit
    /// rather than a measurement; the number of accounts the portal actually holds is reported
    /// separately by <see cref="Users"/>.
    /// </remarks>
    public int? UserQuota { get; set; }

    // MIGRATION: Users and Pages change from lazily queried getters to plainly assigned values.
    // Both are measurements rather than stored columns, and three independent findings establish
    // that.
    //
    // First, the legacy getters queried the database themselves, on first read, from inside a
    // property. The backing fields were initialised to the legacy encoding for "no whole number"
    // (PortalInfo.vb lines 61 and 62), and each getter treated a negative field as "not yet
    // loaded" and replaced it with a live count - the account count at lines 311 to 313 and the
    // page count at lines 322 to 325. Second, neither name appears anywhere in the view that the
    // read path selects through (04.05.00.SqlDataProvider lines 1530 to 1587), so neither is a
    // column of the portal table; both readers issue SELECT * against that view, so a column the
    // view omits cannot reach the object at all. Third, the account count is a query in its own
    // right, declared separately in the membership provider's data abstraction at
    // Library/Providers/MembershipProviders/DataProvider/DataProvider.vb line 82.
    //
    // A property getter cannot be awaited, so a getter that performs a database read cannot be
    // expressed without blocking, and blocking is not permitted anywhere in this codebase.
    // Both members are therefore plain settable values, assigned explicitly by
    // Application/Services/PortalService.cs from the counts it has already awaited through the
    // user and tab repository abstractions. Neither is computed, neither is lazy and neither
    // touches a data store.
    //
    // Neither member is seeded with the legacy "not yet loaded" encoding. That negative value was
    // a private flag inside a getter that no longer exists, and emitting it would tell a consumer
    // that the portal holds a negative number of accounts. Both members simply start at zero and
    // report whatever the service assigns, so neither is ever negative on the wire. Both are
    // non-nullable, because counting rows always yields a number.

    /// <summary>
    /// Gets or sets the number of user accounts currently registered against the portal.
    /// </summary>
    /// <remarks>
    /// Corresponds to the legacy <c>Users</c> property
    /// (<c>Library/Components/Portal/PortalInfo.vb</c> line 309) - which, despite its lazily
    /// loading getter, was a fully readable and writable property with a setter at line 316. This
    /// is a measurement, not a stored column, and not a limit: the permitted maximum is
    /// <see cref="UserQuota"/>. See the note above this member for how the value is populated and
    /// why it is never negative.
    /// </remarks>
    public int Users { get; set; }

    /// <summary>
    /// Gets or sets the number of pages currently defined within the portal.
    /// </summary>
    /// <remarks>
    /// Corresponds to the legacy <c>Pages</c> property
    /// (<c>Library/Components/Portal/PortalInfo.vb</c> line 320) - likewise a fully readable and
    /// writable property, with a setter at line 328. This is a measurement, not a stored column,
    /// and not a limit: the permitted maximum is <see cref="PageQuota"/>. See the note above this
    /// member for how the value is populated and why it is never negative.
    /// </remarks>
    public int Pages { get; set; }


    // MIGRATION: the eight role and page references below - two role identifiers and six page
    // identifiers - all share one hazard, recorded here once rather than eight times.
    //
    // In legacy data an unset reference is stored as the number -1, because that is what the
    // shared helper answered to mean "no whole number" (Library/Components/Shared/Null.vb lines
    // 41 to 45) and what its converter substituted for a database null on every read (line 88).
    // Each of these members is consequently a nullable whole number here, which is the honest
    // wire shape: it can express a reference that is genuinely unset without borrowing a value
    // from the range of real ones. But this contract does NOT quietly rewrite the legacy encoding
    // - a record holding -1 is transported as -1, and the decision whether a mapper forwards that
    // number or substitutes no value at all is made, and documented, in
    // Application/Mapping/PortalMappings.cs.
    //
    // Consumers must not run the inference backwards. The number -1 does not prove a reference is
    // unset, because the legacy absence test reported it as absent for any whole number
    // whatsoever (Null.vb lines 210 to 211) including genuine identifiers. Nor does zero prove
    // anything: the role table and the page table both seed their identity columns at zero
    // (01.00.00.SqlDataProvider lines 115 and 140), so zero is a real role identifier and a real
    // page identifier - and the stock installation demonstrates it, shipping a _default portal
    // whose administrator role identifier IS zero (line 7125). The only sound test for "no
    // reference" on this contract is the absence of a value, and no member of this type offers a
    // helper that claims otherwise.

    /// <summary>
    /// Gets or sets the identifier of the security role whose members administer the portal.
    /// </summary>
    /// <remarks>
    /// Corresponds to the legacy <c>AdministratorRoleId</c> property
    /// (<c>Library/Components/Portal/PortalInfo.vb</c> line 189) and to the
    /// <c>Portals.AdministratorRoleId</c> column, declared <c>[int] NULL</c> at
    /// <c>Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider</c> line 83.
    /// Zero is a legitimate value for this member, and the stock <c>_default</c> portal in fact
    /// ships with it (line 7125), because the role table's identity column is seeded at zero
    /// (line 115). See the note above this member for the legacy encoding of an unset reference.
    /// </remarks>
    public int? AdministratorRoleId { get; set; }

    /// <summary>
    /// Gets or sets the name of the administrator role. Read-only: computed by the read path from
    /// <see cref="AdministratorRoleId"/>, not stored against the portal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Corresponds to the legacy <c>AdministratorRoleName</c> property
    /// (<c>Library/Components/Portal/PortalInfo.vb</c> line 197), which held plain text.
    /// </para>
    /// <para>
    /// The value is not a column. The view the read path selects through resolves it with a
    /// correlated sub-select against the role table, at
    /// <c>Website/Providers/DataProviders/SqlDataProvider/04.05.00.SqlDataProvider</c> line 1584:
    /// <c>(SELECT TOP 1 RoleName FROM ...Roles WHERE (RoleID = P.AdministratorRoleId))</c>. It is
    /// therefore a convenience label that always follows the identifier, it cannot be written
    /// back, and it yields no value when the identifier matches no role. This is why the legacy
    /// update entry point omits it from its argument list. Only the name is carried: no role
    /// contract is nested here.
    /// </para>
    /// </remarks>
    public string? AdministratorRoleName { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the security role granted to every registered user of the
    /// portal.
    /// </summary>
    /// <remarks>
    /// Corresponds to the legacy <c>RegisteredRoleId</c> property
    /// (<c>Library/Components/Portal/PortalInfo.vb</c> line 205) and to the
    /// <c>Portals.RegisteredRoleId</c> column, declared <c>[int] NULL</c> at
    /// <c>Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider</c> line 84.
    /// As with <see cref="AdministratorRoleId"/>, zero is a legitimate role identifier because the
    /// role table's identity column is seeded at zero (line 115). See the note above these members
    /// for the legacy encoding of an unset reference.
    /// </remarks>
    public int? RegisteredRoleId { get; set; }

    /// <summary>
    /// Gets or sets the name of the registered-users role. Read-only: computed by the read path
    /// from <see cref="RegisteredRoleId"/>, not stored against the portal.
    /// </summary>
    /// <remarks>
    /// Corresponds to the legacy <c>RegisteredRoleName</c> property
    /// (<c>Library/Components/Portal/PortalInfo.vb</c> line 213), which held plain text. As with
    /// <see cref="AdministratorRoleName"/> it is not a column: the view resolves it with a
    /// correlated sub-select against the role table, at
    /// <c>Website/Providers/DataProviders/SqlDataProvider/04.05.00.SqlDataProvider</c> line 1585:
    /// <c>(SELECT TOP 1 RoleName FROM ...Roles WHERE (RoleID = P.RegisteredRoleId))</c>. It cannot
    /// be written back, and it yields no value when the identifier matches no role.
    /// </remarks>
    public string? RegisteredRoleName { get; set; }

    /// <summary>
    /// Gets or sets the portal's stable global identifier, assigned once when the portal row is
    /// created.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Corresponds to the legacy <c>GUID</c> property
    /// (<c>Library/Components/Portal/PortalInfo.vb</c> line 245) and to the
    /// <c>Portals.GUID</c> column, declared
    /// <c>[uniqueidentifier] NOT NULL CONSTRAINT DF_Portals_GUID DEFAULT newid()</c> at
    /// <c>Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider</c> line 91.
    /// The column is not nullable and the database generates the value, so this member is a plain
    /// non-nullable identifier. The stock <c>_default</c> portal ships with a fixed one (line
    /// 7125).
    /// </para>
    /// <para>
    /// Unlike every other member here, the legacy property was excluded from the portal-template
    /// document: it carried the ignore attribute rather than an element name, since a global
    /// identifier belongs to one installation and copying it into another would duplicate it.
    /// This contract is JSON rather than a portal template and reports the value plainly. The
    /// member is renamed from the legacy all-capitals spelling, as recorded at the head of this
    /// class.
    /// </para>
    /// <para>
    /// The database generator never produces the all-zero identifier, so a genuine row always
    /// carries a real one. The legacy shared helper nevertheless treated the all-zero identifier
    /// as its encoding for an absent identifier
    /// (<c>Library/Components/Shared/Null.vb</c> lines 81 to 85, honoured by its absence test at
    /// line 230), so a value that travelled the legacy path could be the all-zero one. A
    /// non-nullable identifier is the schema-faithful choice, and no emptiness guard is applied
    /// here.
    /// </para>
    /// </remarks>
    public Guid Guid { get; set; }

    /// <summary>
    /// Gets or sets the name of the payment gateway through which the portal takes payments.
    /// </summary>
    /// <remarks>
    /// Corresponds to the legacy <c>PaymentProcessor</c> property
    /// (<c>Library/Components/Portal/PortalInfo.vb</c> line 253). This is the gateway's name
    /// only, not a credential, which is why it is safe to report. Its companion account
    /// identifier is <see cref="ProcessorUserId"/>; the third part of the legacy gateway
    /// configuration, the password, is deliberately absent from this contract for the reason
    /// recorded at the head of this class.
    /// </remarks>
    public string? PaymentProcessor { get; set; }

    /// <summary>
    /// Gets or sets the portal's account identifier at the payment gateway.
    /// </summary>
    /// <remarks>
    /// Corresponds to the legacy <c>ProcessorUserId</c> property
    /// (<c>Library/Components/Portal/PortalInfo.vb</c> line 269), and it is text rather than a
    /// number because a gateway account identifier is an opaque string. In the legacy update
    /// entry point this argument immediately precedes the gateway password
    /// (<c>Library/Components/Portal/PortalController.vb</c> line 1568, arguments fifteen and
    /// sixteen); this contract reports the account identifier and stops there, so the two must not
    /// be assumed to travel together.
    /// </remarks>
    public string? ProcessorUserId { get; set; }

    /// <summary>
    /// Gets or sets the number of days of site-activity history retained for the portal.
    /// </summary>
    /// <remarks>
    /// Corresponds to the legacy <c>SiteLogHistory</c> property
    /// (<c>Library/Components/Portal/PortalInfo.vb</c> line 277). The unit is days, not rows: the
    /// legacy administration screen labelled the field "Site Log History (Days):" and described it
    /// as the number of days of site activity kept for the site. The member is a nullable whole
    /// number because a portal need not have a retention period recorded.
    /// </remarks>
    public int? SiteLogHistory { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the page that hosts the portal's administration menu.
    /// </summary>
    /// <remarks>
    /// Corresponds to the legacy <c>AdminTabId</c> property
    /// (<c>Library/Components/Portal/PortalInfo.vb</c> line 293). Zero is a legitimate page
    /// identifier, because the page table's identity column is seeded at zero
    /// (<c>Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider</c> line 140).
    /// See the note above the role identifiers for the legacy encoding of an unset reference. Only
    /// the identifier is carried: no page contract is nested here, and a caller that needs the
    /// page itself fetches it from the pages resource.
    /// </remarks>
    public int? AdminTabId { get; set; }

    // MIGRATION: SuperTabId is not a portal attribute and is not per-portal at all. The view the
    // read path selects through computes it with an uncorrelated sub-select that names no portal,
    // at 04.05.00.SqlDataProvider line 1583:
    //
    //     (SELECT TOP 1 TabID FROM ...Tabs
    //      WHERE (PortalID IS NULL) AND (ParentId IS NULL)) AS SuperTabId
    //
    // Because the predicate selects the host-level root page - the one page belonging to no
    // portal and having no parent - the sub-select yields THE SAME VALUE FOR EVERY PORTAL. It is
    // reported here for parity with the legacy object, it is read-only, and it must not be
    // mistaken for something a portal owns or an administrator can change. It is one of the two
    // reasons the legacy update entry point omits it. When no such page exists the sub-select
    // yields nothing, which the legacy reader then converted into its encoding for an absent
    // whole number.

    /// <summary>
    /// Gets or sets the identifier of the host-level root page. Read-only, and identical for every
    /// portal: computed by the read path, not stored against the portal.
    /// </summary>
    /// <remarks>
    /// Corresponds to the legacy <c>SuperTabId</c> property
    /// (<c>Library/Components/Portal/PortalInfo.vb</c> line 301). See the note above this member
    /// for the measured derivation, and the note above the role identifiers for the legacy encoding
    /// of an unresolved reference. Zero is a legitimate page identifier here as elsewhere.
    /// </remarks>
    public int? SuperTabId { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the page shown to a first-time visitor before the portal's
    /// home page.
    /// </summary>
    /// <remarks>
    /// Corresponds to the legacy <c>SplashTabId</c> property
    /// (<c>Library/Components/Portal/PortalInfo.vb</c> line 332). A portal need not define a
    /// splash page, so the member is a nullable whole number. Zero is a legitimate page
    /// identifier; see the note above the role identifiers for the legacy encoding of an unset
    /// reference.
    /// </remarks>
    public int? SplashTabId { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the portal's home page.
    /// </summary>
    /// <remarks>
    /// Corresponds to the legacy <c>HomeTabId</c> property
    /// (<c>Library/Components/Portal/PortalInfo.vb</c> line 340). A portal need not nominate an
    /// explicit home page, so the member is a nullable whole number. Zero is a legitimate page
    /// identifier; see the note above the role identifiers for the legacy encoding of an unset
    /// reference.
    /// </remarks>
    public int? HomeTabId { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the page carrying the portal's sign-in form.
    /// </summary>
    /// <remarks>
    /// Corresponds to the legacy <c>LoginTabId</c> property
    /// (<c>Library/Components/Portal/PortalInfo.vb</c> line 348). A portal need not nominate a
    /// dedicated sign-in page, so the member is a nullable whole number. Zero is a legitimate page
    /// identifier; see the note above the role identifiers for the legacy encoding of an unset
    /// reference.
    /// </remarks>
    public int? LoginTabId { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the page carrying the portal's user-account form.
    /// </summary>
    /// <remarks>
    /// Corresponds to the legacy <c>UserTabId</c> property
    /// (<c>Library/Components/Portal/PortalInfo.vb</c> line 356). A portal need not nominate a
    /// dedicated user page, so the member is a nullable whole number. Zero is a legitimate page
    /// identifier; see the note above the role identifiers for the legacy encoding of an unset
    /// reference.
    /// </remarks>
    public int? UserTabId { get; set; }

    /// <summary>
    /// Gets or sets the culture code the portal presents by default.
    /// </summary>
    /// <remarks>
    /// Corresponds to the legacy <c>DefaultLanguage</c> property
    /// (<c>Library/Components/Portal/PortalInfo.vb</c> line 364), presented by the legacy
    /// administration screen as "Default Language:" and populated from a culture list. The value
    /// is transported as plain text exactly as stored and is neither parsed nor normalised here.
    /// An empty value may legitimately appear where a modern consumer would expect none.
    /// </remarks>
    public string? DefaultLanguage { get; set; }

    // MIGRATION: TimeZoneOffset keeps the legacy property's spelling, with a capital Z, in
    // preference to the stored column's spelling, which has a lower-case z. The column is
    // projected as "TimezoneOffset" by the view at 04.05.00.SqlDataProvider line 1576, whereas the
    // legacy property is TimeZoneOffset (PortalInfo.vb line 372). The two never conflicted because
    // database identifiers are matched without regard to case. Modern C# and the client model are
    // both case-sensitive, so one spelling had to be chosen: the property spelling wins because it
    // is the correct casing for the term and because it is the name the legacy code, and therefore
    // every reader of that code, already used. The column itself is untouched; the entity
    // configuration in the infrastructure layer binds the stored spelling.

    /// <summary>
    /// Gets or sets the portal's offset from Coordinated Universal Time, in minutes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Corresponds to the legacy <c>TimeZoneOffset</c> property
    /// (<c>Library/Components/Portal/PortalInfo.vb</c> line 372), presented by the legacy
    /// administration screen as "Portal TimeZone:". The unit is minutes, not hours and not
    /// seconds: the legacy time-conversion helper adds the stored value directly as minutes, at
    /// <c>Library/Components/Users/UserTime.vb</c> lines 33 and 63. Minutes are necessary rather
    /// than merely conventional, because several real zones are offset by a fraction of an hour.
    /// </para>
    /// <para>
    /// The member is a nullable whole number, and a negative value is ordinary here rather than
    /// exceptional: zones west of the prime meridian are offset by a negative number of minutes.
    /// The legacy code did apply its absent-value test to this field
    /// (<c>Library/Components/Portal/PortalSettings.vb</c> line 670), so the legacy encoding for
    /// an absent whole number can appear in stored data and would be indistinguishable from a
    /// genuine one-minute-west offset. This contract transports whichever it is handed and
    /// converts in neither direction.
    /// </para>
    /// </remarks>
    public int? TimeZoneOffset { get; set; }

    /// <summary>
    /// Gets or sets the portal's home directory, relative to the application, under which its
    /// uploaded content is kept.
    /// </summary>
    /// <remarks>
    /// Corresponds to the legacy <c>HomeDirectory</c> property
    /// (<c>Library/Components/Portal/PortalInfo.vb</c> line 380), presented by the legacy
    /// administration screen as "Home Directory:". The value is portal-relative, and that is the
    /// whole of what this contract reports: the legacy class also derived an absolute server
    /// filesystem path from it, and that derived property is deliberately absent here for the
    /// reason recorded at the head of this class. Resolving this value against a physical location
    /// is a server concern and stays on the server.
    /// </remarks>
    public string? HomeDirectory { get; set; }

    // MIGRATION: the legacy alias collection produces no counterpart type. Aliases were carried by
    // a hand-written pre-generic dictionary wrapper keyed by the lower-cased alias
    // (Library/Components/Portal/PortalAliasCollection.vb), and that wrapper is not ported: a
    // read-only generic list expresses the same thing natively, so the collection class, its
    // untyped predecessors and their indexers all disappear rather than being translated. The
    // member is declared as a read-only generic list rather than a mutable one, an array or a
    // lazily evaluated sequence, so that a received payload cannot be mutated in place and cannot
    // hide deferred work behind an enumeration.

    /// <summary>
    /// Gets or sets the host names through which this portal is reached, when they have been
    /// loaded for this response.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This member has no counterpart on the legacy portal class; the legacy screens fetched
    /// aliases separately. It composes <see cref="PortalAliasDto"/>, which is declared alongside
    /// this contract and describes one alias.
    /// </para>
    /// <para>
    /// The member distinguishes three honest states, and a consumer must tell them apart rather
    /// than collapsing the first two:
    /// </para>
    /// <list type="bullet">
    ///   <item>
    ///     <description>
    ///     No value at all means the aliases were not requested or not loaded on this call. It
    ///     says nothing whatever about how many aliases the portal has. Nullability exists
    ///     precisely so that the portal service may skip the alias query on the paths that do not
    ///     need it without the payload having to misreport the result.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///     An empty collection means the aliases were loaded and the portal genuinely has none.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <description>
    ///     A populated collection means the aliases were loaded and these are they.
    ///     </description>
    ///   </item>
    /// </list>
    /// <para>
    /// When no value is present, a caller obtains the aliases from the dedicated sub-resource
    /// <c>GET /api/v1/portals/{id}/aliases</c>, served by <c>PortalAliasesController</c>, which
    /// remains the authoritative place to list, add and remove them.
    /// </para>
    /// <para>
    /// The collection is assigned by the portal service from a set it has already materialised.
    /// It is never fetched from this member, which performs no work of any kind.
    /// </para>
    /// </remarks>
    public IReadOnlyList<PortalAliasDto>? Aliases { get; set; }
}
