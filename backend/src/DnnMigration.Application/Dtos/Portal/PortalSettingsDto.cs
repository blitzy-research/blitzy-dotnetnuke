using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Dtos.Portal;

/// <summary>
/// Wire contract for one portal's configuration, as the legacy Site Settings screen presented it.
/// </summary>
/// <remarks>
/// <para>
/// Intended for <c>GET /api/v1/portals/{id}/settings</c> and for the client's
/// <c>features/portal/portal-settings/</c> screen, where the legacy multi-step wizard becomes a
/// single tabbed view.
/// </para>
/// <para>
/// A COLUMN PROJECTION, not a name-and-value collection. Every member is one column of the
/// <c>Portals</c> table, because no portal-level setting record exists in this system to model as a
/// keyed collection: the legacy abstract data surface declares no portal-setting member among its
/// 269 methods, the concrete provider invokes no portal-setting procedure among its 245, no
/// portal-settings table is created anywhere in the 88-script schema chain, and the legacy accessor
/// at <c>Library/Components/Portal/PortalController.vb:L1209-L1211</c> pulled its
/// <c>PortalSettings</c> object from per-request ambient state rather than from a store. Module-level
/// and tab-module-level settings genuinely are keyed collections here, backed by real tables;
/// portal-level configuration is not.
/// </para>
/// <para>
/// Inert by design: no validation, clamping, normalisation, computed member, conversion or store
/// access. The legacy behaviour that surrounded these values belongs elsewhere, and an implementer
/// is obliged to place it there. The two comparisons the legacy screen declared - a date check on
/// the expiry field at <c>Website/admin/Portal/sitesettings.ascx:L433</c> and a currency check on
/// the hosting fee at <c>:L444</c> - belong to an update-request validator under
/// <c>Application/Validation/</c>; note that the screen declared no required-field check at all and
/// none whatever on the disk-space field, so requiredness must not be inferred from this contract's
/// nullability. Translation to and from the persisted record belongs to a hand-written portal mapper
/// under <c>Application/Mapping/</c>. The rule that locks portal-level banner editing when the mode
/// is host-managed (<c>SiteSettings.ascx.vb:L295-L296</c>) and the guard that refuses a
/// non-super-user's change to the fee, disk space, quotas, log retention or expiry date
/// (<c>:L757-L770</c>) both belong to the portal service; no editability flag appears below.
/// </para>
/// <para>
/// No persisted entity is exposed here in either direction, which is what allows the legacy sentinel
/// semantics below to be honoured at the API edge without contaminating the model behind it. No
/// identifier-wrapper value type appears on any member either: those wrappers expose their payload
/// through a nested property and carry no serialisation converter, so a client would receive an
/// object where it expects a number or a string. Plain primitives throughout.
/// </para>
/// <para>
/// THE LEGACY NULL CONTRACT, and why it is visible here. The shared sentinel helper at
/// <c>Library/Components/Shared/Null.vb</c> encodes absence in band rather than as a database null:
/// -1 for an integer (L41-L45), the empty string for a string (L71-L75), the minimum date for a date
/// (L66-L70) and the all-zero identifier (L81-L85). Its converters applied that encoding on every
/// read (L88, L119) and its absence test reports each as absent (L208-L237), so those values are
/// externally observable in legacy data and may legitimately arrive on the members below. This
/// contract transports whichever value it is handed and converts in neither direction; reconciling a
/// sentinel with a null is a mapper decision. Nothing below treats any numeric or string value as
/// meaning absence.
/// </para>
/// <para>
/// THE IDENTIFIER TRAP. <c>Portals.PortalID</c> is <c>[int] IDENTITY (-1, 1) NOT NULL</c>
/// (<c>Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider:L77</c>) and the
/// installation ships a portal whose key is zero (<c>:L7125</c>), so the identity seed is itself the
/// legacy absent-integer sentinel. Both values are real, addressable portal identifiers. Sibling
/// tables seed differently again - roles, tabs and modules from zero, users from one - so no single
/// numeric convention for absence exists in this schema at all.
/// </para>
/// <para>
/// Wording. Every label and help text quoted below comes from
/// <c>Website/admin/Portal/App_LocalResources/SiteSettings.ascx.resx</c>, the authoritative record of
/// what the legacy screen displayed, so client labels stay recognisable to existing administrators.
/// </para>
/// </remarks>
public sealed class PortalSettingsDto
{
    // MIGRATION: no member of the legacy per-request PortalSettings composite appears here. That type
    //   was assembled per request from ambient state (PortalController.vb:L1209-L1211) and its mutable
    //   current-page member (settable at PortalSettings.vb:L398) is request state, which has no place
    //   on a wire contract; the members that genuinely describe the request survive instead as an
    //   immutable per-request context abstraction in the Domain layer. Nothing here derives from
    //   ambient request state, and nothing here is a host-level setting: the two host-prefixed members
    //   below are real Portals columns that carry that prefix in the schema.

    // MIGRATION: the legacy PortalInfo class carried 39 properties; this configuration projection
    //   carries 27. The twelve that are absent are exactly the twelve the legacy update path -- the
    //   twenty-seven-argument PortalController.UpdatePortalInfo at PortalController.vb:L1568 --
    //   also excludes, and each is absent for a measured reason:
    //
    //     Email, AdministratorRoleName, RegisteredRoleName, SuperTabId
    //         Not Portals columns at all. They reach the legacy read path only because that path is
    //         the view vw_Portals (terminal definition at 04.05.00.SqlDataProvider:L1530-L1587, read
    //         wholesale by GetPortal at 04.04.00.SqlDataProvider:L199 and GetPortals at :L230). The
    //         address is the ADMINISTRATOR's, joined from the user table by
    //         "LEFT OUTER JOIN ...Users AS U ON P.AdministratorId = U.UserID" (L1586-L1587) and
    //         projected at L1574; the other three are view sub-selects (L1583 to L1585), of which the
    //         root host tab is identical for every portal. All four belong to PortalDetailDto as
    //         read-only view-derived members.
    //
    //     Users, Pages, Version, HomeDirectoryMapPath
    //         Absent from vw_Portals entirely, which independently confirms none is persisted portal
    //         state. Version is not a Portals column - the only 8-character string column of that name
    //         in the chain belongs to the DesktopModules table (02.00.00.SqlDataProvider:L5144, inside
    //         the create-table opening at L5140). HomeDirectoryMapPath was a read-only filesystem path
    //         the legacy class computed from the excluded application-path module (PortalInfo.vb:L388).
    //
    //     AdministratorRoleId, RegisteredRoleId, AdminTabId
    //         Real Portals columns, projected by the view. Excluded here because neither authority for
    //         this contract admits them: no control for any of them appears on sitesettings.ascx, and
    //         none appears among the twenty-seven arguments. Reproducing them on a settings projection
    //         would invent an affordance the legacy application did not offer. They belong to
    //         PortalDetailDto.
    //
    //     GUID
    //         Excluded from the update path only, because the database owns the value. It IS displayed
    //         by the settings screen and is therefore present below.
    //
    //   The XML serialisation decoration the legacy class carried on almost every property, including
    //   its class-level element name at PortalInfo.vb:L29, is dropped rather than translated:
    //   serialisation happens at the API boundary only, and this contract states the wire shape
    //   through its member names alone.

    // MIGRATION: the legacy Site Settings screen also offered fields that are absent from every Portal
    //   contract here, because the screen mixed portal configuration with settings belonging to
    //   excluded subsystems - a search-provider selector, an inline-editing toggle, four
    //   transport-security fields, a stylesheet field, a site-map field, a search-engine submission
    //   field, three control-panel option lists, four skin and container pickers and a desktop-module
    //   assignment list. Independently of the exclusion list, not one of them appears among the
    //   twenty-seven arguments either, which confirms none is a Portals column reachable by this work.

    /// <summary>
    /// Gets or sets the identifier of the portal this configuration describes.
    /// </summary>
    /// <remarks>
    /// Legacy <c>PortalID</c> (<c>PortalInfo.vb:L85</c>), first of the twenty-seven arguments, and
    /// <c>Portals.PortalID</c>, declared <c>[int] IDENTITY (-1, 1) NOT NULL</c> at
    /// <c>01.00.00.SqlDataProvider:L77</c>. Not nullable, because a settings response always describes
    /// an existing portal. Every value it can hold identifies a portal; see the identifier trap in the
    /// type-level remarks.
    /// </remarks>
    public int PortalId { get; set; }

    /// <summary>
    /// Gets or sets the portal's display name, which the legacy screen labelled "Title:".
    /// </summary>
    /// <remarks>
    /// Legacy <c>PortalName</c> (<c>PortalInfo.vb:L93</c>), the <c>txtPortalName</c> text box, and
    /// <c>Portals.PortalName</c>, declared <c>[nvarchar] (128) NOT NULL</c> at
    /// <c>01.00.00.SqlDataProvider:L79</c>. Nullable even though the column is not, because a
    /// projection reports what it was handed and invents nothing. An empty string may legitimately
    /// appear where a modern reader would expect no value: the legacy null contract encodes an absent
    /// string as the empty string, and the two are indistinguishable in legacy data.
    /// </remarks>
    public string? PortalName { get; set; }

    /// <summary>
    /// Gets or sets the portal's descriptive text.
    /// </summary>
    /// <remarks>
    /// Legacy <c>Description</c> (<c>PortalInfo.vb:L221</c>), the <c>txtDescription</c> text box, and
    /// <c>Portals.Description</c>, added <c>nvarchar(500) NULL</c> at
    /// <c>01.00.02.SqlDataProvider:L884</c>. An empty string may legitimately mean no value, per the
    /// legacy null contract described at type level.
    /// </remarks>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the portal's search keywords, separated by commas.
    /// </summary>
    /// <remarks>
    /// Legacy <c>KeyWords</c> (<c>PortalInfo.vb:L229</c>), the <c>txtKeyWords</c> text box, and
    /// <c>Portals.KeyWords</c>, added <c>nvarchar(500) NULL</c> at
    /// <c>01.00.02.SqlDataProvider:L885</c>. The internal capital W is the spelling of both the legacy
    /// property and the column, kept so a client model can reuse this identifier verbatim. Nothing here
    /// parses, splits or trims the list. An empty string may legitimately mean no value.
    /// </remarks>
    public string? KeyWords { get; set; }

    /// <summary>
    /// Gets or sets the footer text, which the legacy screen labelled "Copyright:".
    /// </summary>
    /// <remarks>
    /// Legacy <c>FooterText</c> (<c>PortalInfo.vb:L109</c>), the <c>txtFooterText</c> text box, and
    /// <c>Portals.FooterText</c>, declared <c>[nvarchar] (100) NULL</c> at
    /// <c>01.00.00.SqlDataProvider:L82</c>. Whether the value is rendered was a skinning concern and
    /// skinning is excluded; the value itself is portal configuration and is carried. An empty string
    /// may legitimately mean no value.
    /// </remarks>
    public string? FooterText { get; set; }

    // MIGRATION: the two file members below are TRANSFORMED ON READ, and the asymmetry is a genuine
    //   round-trip hazard that this contract cannot resolve and must therefore document.
    //
    //   The terminal read path is not the Portals table but the view vw_Portals, which both read
    //   procedures select from wholesale (GetPortal at 04.04.00.SqlDataProvider:L199, GetPortals at
    //   :L230). At 04.05.00.SqlDataProvider:L1535-L1544 the view rewrites the logo column:
    //
    //       CASE WHEN LEFT(LOWER(LogoFile), 6) = 'fileid'
    //            THEN (SELECT Folder + FileName FROM ...Files
    //                  WHERE 'fileid=' + convert(varchar, ...Files.FileID) = LogoFile)
    //            ELSE LogoFile END AS LogoFile
    //
    //   and applies a character-for-character identical rewrite to the background column at
    //   L1559-L1568.
    //
    //   So the STORED value may be a file-identifier token while the value READ BACK is the resolved
    //   folder-and-filename path the token pointed at. Echoing a value read from this contract straight
    //   into an update overwrites the token with a path and permanently severs the link to the file
    //   record. A consumer meaning to leave a file reference untouched must OMIT the member from its
    //   update rather than round-trip it.
    //
    //   The hazard is inherited, not introduced: the legacy screen itself assigned the picker's
    //   resolved value back into the update (SiteSettings.ascx.vb:L698-L699) and compared it against
    //   the read value at :L701. Per the domain-logic-preservation clause the defect is annotated and
    //   NOT repaired, since repairing it would change behaviour. No token resolution is implemented
    //   here: resolving a token is repository and mapper territory, and the legacy file-system subtree
    //   it reaches into is excluded from this migration.

    /// <summary>
    /// Gets or sets the portal logo image reference, which the legacy screen labelled "Logo:".
    /// </summary>
    /// <remarks>
    /// Legacy <c>LogoFile</c> (<c>PortalInfo.vb:L101</c>), the <c>ctlLogo</c> picker at
    /// <c>sitesettings.ascx:L140</c>, and <c>Portals.LogoFile</c>, declared <c>[nvarchar] (50) NULL</c>
    /// at <c>01.00.00.SqlDataProvider:L81</c>. The value read here is the RESOLVED
    /// folder-and-filename path whenever the stored value was a file-identifier token; read the
    /// migration note above this member before writing it back. An empty string may also legitimately
    /// mean no value.
    /// </remarks>
    public string? LogoFile { get; set; }

    /// <summary>
    /// Gets or sets the page background image reference, which the legacy screen labelled "Body
    /// Background:".
    /// </summary>
    /// <remarks>
    /// Legacy <c>BackgroundFile</c> (<c>PortalInfo.vb:L237</c>), the <c>ctlBackground</c> picker at
    /// <c>sitesettings.ascx:L150</c>, and <c>Portals.BackgroundFile</c>, added <c>nvarchar(50) NULL</c>
    /// at <c>01.00.02.SqlDataProvider:L886</c>. The migration note above <see cref="LogoFile"/> applies
    /// verbatim, because the view rewrite is identical.
    /// </remarks>
    public string? BackgroundFile { get; set; }

    /// <summary>
    /// Gets or sets the date on which the portal's hosting contract expires.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy <c>ExpiryDate</c> (<c>PortalInfo.vb:L117</c>, a VB <c>Date</c>), the
    /// <c>txtExpiryDate</c> text box, and <c>Portals.ExpiryDate</c>, declared <c>[datetime] NULL</c> at
    /// <c>01.00.00.SqlDataProvider:L83</c>. The legacy screen declared a date-type comparison on the
    /// field (<c>sitesettings.ascx:L433</c>, message "Invalid expiry date!"), which an update-request
    /// validator must reproduce.
    /// </para>
    /// <para>
    /// SENTINEL NOTE, and it matters more here than anywhere else on this contract. The legacy screen
    /// initialised the value it was about to submit to the absent-date sentinel
    /// (<c>SiteSettings.ascx.vb:L733</c>), which is the minimum date
    /// (<c>Library/Components/Shared/Null.vb:L66-L70</c>) - and the legacy absence test compares only
    /// the DATE PART (<c>Null.vb:L222-L224</c>), so any instant on the first day of year one was
    /// treated as absent regardless of its time component. That value may arrive on this member and is
    /// not converted to a null here.
    /// </para>
    /// </remarks>
    public DateTime? ExpiryDate { get; set; }

    /// <summary>
    /// Gets or sets the mode by which the portal admits new user accounts.
    /// </summary>
    /// <remarks>
    /// Legacy <c>UserRegistration</c> (<c>PortalInfo.vb:L125</c>), a bare integer discriminator, and
    /// the <c>optUserRegistration</c> option list at <c>sitesettings.ascx:L228-L234</c>, whose four
    /// items carry the values zero to three under the labels "None", "Private", "Public" and
    /// "Verified". Typed as <see cref="UserRegistrationMode"/> so the discriminator is named at the
    /// boundary rather than transported as a magic number; the enumeration serialises as its numeric
    /// value, which is byte-identical to the stored column and to what the legacy list posted, so
    /// naming it costs no change in wire shape. Not nullable, because the terminal column is
    /// <c>[int] NOT NULL</c> defaulting to zero (<c>03.01.01.SqlDataProvider:L1116</c>, default at
    /// <c>:L1125</c>) - so zero is a chosen mode, not a stand-in for a missing value.
    /// </remarks>
    public UserRegistrationMode UserRegistration { get; set; }

    /// <summary>
    /// Gets or sets the portal's banner-advertising mode.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy <c>BannerAdvertising</c> (<c>PortalInfo.vb:L133</c>), a bare integer discriminator, and
    /// the <c>optBanners</c> option list at <c>sitesettings.ascx:L121-L126</c>, whose three items carry
    /// the values zero to two under the labels "None", "Site" and "Host". Typed as
    /// <see cref="BannerAdvertisingMode"/> for the same reason as <see cref="UserRegistration"/>. Not
    /// nullable: the terminal column is <c>[int] NOT NULL</c> defaulting to zero
    /// (<c>03.01.01.SqlDataProvider:L1117</c>, default at <c>:L1127</c>).
    /// </para>
    /// <para>
    /// One live legacy rule keys off the host-managed member and is deliberately NOT implemented here.
    /// At <c>SiteSettings.ascx.vb:L295-L296</c> the screen disabled portal-level banner editing and
    /// revealed an explanatory label whenever the stored mode was host-managed, and only for a caller
    /// who was not a super user. An implementer of the portal service and of the client form is obliged
    /// to reproduce it; this contract reports the mode and offers no editability flag.
    /// </para>
    /// </remarks>
    public BannerAdvertisingMode BannerAdvertising { get; set; }

    /// <summary>
    /// Gets or sets the three-letter currency code used for the portal's monetary values.
    /// </summary>
    /// <remarks>
    /// Legacy <c>Currency</c> (<c>PortalInfo.vb:L149</c>), the <c>cboCurrency</c> selector, and
    /// <c>Portals.Currency</c>, declared <c>[char] (3) NULL</c> at
    /// <c>01.00.00.SqlDataProvider:L88</c>. The three-character limit is recorded here for an
    /// update-request validator to enforce and is deliberately not declared as an attribute. Because
    /// the column is fixed-width a legacy value may carry trailing padding; this contract neither trims
    /// nor pads. An empty string may legitimately mean no value.
    /// </remarks>
    public string? Currency { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the user account that administers the portal.
    /// </summary>
    /// <remarks>
    /// Legacy <c>AdministratorId</c> (<c>PortalInfo.vb:L141</c>), the <c>cboAdministratorId</c>
    /// selector, and <c>Portals.AdministratorId</c>, declared <c>[int] NULL</c> at
    /// <c>01.00.00.SqlDataProvider:L86</c>. It is a foreign key to the user record, which the terminal
    /// read view joins on (<c>04.05.00.SqlDataProvider:L1587</c>). Nullable, matching the column. Every
    /// value it can hold is a meaningful user identifier - the user identity column seeds from one
    /// (<c>01.00.00.SqlDataProvider:L98</c>) - so absence must not be inferred from any number.
    /// </remarks>
    public int? AdministratorId { get; set; }

    // MIGRATION: the hosting fee carries a genuine THREE-WAY type conflict, and the terminal schema
    //   decides it:
    //
    //     1. the legacy class  -- PortalInfo.vb:L157 declares the property As Single;
    //     2. the legacy setter -- the twenty-seven-argument PortalController.UpdatePortalInfo at
    //                             PortalController.vb:L1568 declares the argument As Double, because
    //                             the screen parsed its text box into that wider type first
    //                             (SiteSettings.ascx.vb:L704-L707);
    //     3. the schema        -- ALTER COLUMN [HostFee] [money] NOT NULL at
    //                             03.01.01.SqlDataProvider:L1118, default zero at :L1129.
    //
    //   The schema is authoritative because the schema is immutable here and every mapping binds to it.
    //   Money is an exact scaled type, so the wire type is a nullable decimal: adopting either binary
    //   floating-point type would introduce representation error into a value the database stores
    //   exactly. Two corroborations that money is right: the chain migrated this column from a
    //   10-character string to money by an explicit conversion (01.00.05.SqlDataProvider:L1412) and
    //   every terminal stored-procedure parameter for it is declared money; and the legacy screen
    //   validated the field as a currency value (sitesettings.ascx:L444-L446).
    //
    //   No clamp is applied. The inline fee clamps at PortalController.vb:L395 and :L398 act on ROLE
    //   fees inside a role-creation helper, not on this column, and clamping is service behaviour
    //   regardless: a data carrier does not arbitrate values.

    /// <summary>
    /// Gets or sets the monthly monetary charge for hosting the portal, denominated in
    /// <see cref="Currency"/>.
    /// </summary>
    /// <remarks>
    /// Legacy <c>HostFee</c> (<c>PortalInfo.vb:L157</c>), the <c>txtHostFee</c> text box, and
    /// <c>Portals.HostFee</c>. The monthly period and the monetary unit are quoted, not inferred:
    /// <c>plHostFee.Help</c> reads "The Hosting Fee is the monthly charge for hosting this site."
    /// Typed as a nullable decimal to match the exact scaled money column; see the migration note above
    /// for the three-way resolution. The column is not nullable and defaults to zero, so zero is a
    /// chosen fee rather than a marker for a missing one - the wire type is nullable only because a
    /// projection reports what it was handed. A change here was privileged in the legacy application
    /// (<c>SiteSettings.ascx.vb:L757-L770</c> refused a non-super-user), which is a service and
    /// authorisation-policy concern.
    /// </remarks>
    public decimal? HostFee { get; set; }

    // MIGRATION: the disk-space allowance has the same THREE-WAY conflict shape as the hosting fee
    //   above, but it resolves to a DIFFERENT type, so the two must not be assumed to match:
    //
    //     1. the legacy class  -- PortalInfo.vb:L165 declares the property As Integer;
    //     2. the legacy setter -- the twenty-seven-argument setter at PortalController.vb:L1568
    //                             declares the argument As Double, because the screen parsed its text
    //                             box into that wider type first (SiteSettings.ascx.vb:L709-L712);
    //     3. the schema        -- ALTER COLUMN [HostSpace] [int] NOT NULL at
    //                             03.01.01.SqlDataProvider:L1119, default zero at :L1131.
    //
    //   The terminal alteration is an integer column sitting on the line immediately after the money
    //   alteration for the fee, and every terminal stored-procedure parameter for it is declared int --
    //   twelve as a plain integer and fifteen as an integer defaulted to null; not one is declared
    //   money anywhere in the 88-script chain. The rebuild at 01.00.05.SqlDataProvider:L1412
    //   corroborates it a third way, wrapping the fee in an explicit conversion to money while carrying
    //   this column across UNCONVERTED. Schema wins, so this member is a nullable integer and the fee
    //   is a nullable decimal.

    /// <summary>
    /// Gets or sets the disk-space allowance for the portal, in megabytes, where ZERO DENOTES AN
    /// UNLIMITED allowance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy <c>HostSpace</c> (<c>PortalInfo.vb:L165</c>), the <c>txtHostSpace</c> text box, and
    /// <c>Portals.HostSpace</c>. The unit and the meaning of zero are quoted, not inferred:
    /// <c>plHostSpace.Help</c> reads "The amount of Disk Space in MB allowed for this site (enter 0 for
    /// unlimited space)."
    /// </para>
    /// <para>
    /// Zero is therefore load-bearing, and this member is the clearest illustration on this contract of
    /// why absence must never be inferred from a number: here zero means the opposite of "nothing
    /// allowed", and a consumer must render and transmit it as given. Typed as a nullable integer to
    /// match the measured column; see the migration note above. The legacy screen declared no validator
    /// whatever on this field, so the absence of one downstream is faithful rather than an oversight.
    /// As with the fee, a change here was refused for a non-super-user
    /// (<c>SiteSettings.ascx.vb:L757-L770</c>), which is a service concern.
    /// </para>
    /// </remarks>
    public int? HostSpace { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of pages the portal may contain.
    /// </summary>
    /// <remarks>
    /// Legacy <c>PageQuota</c> (<c>PortalInfo.vb:L173</c>), the <c>txtPageQuota</c> text box, and
    /// <c>Portals.PageQuota</c>, added <c>int NOT NULL ... DEFAULT 0</c> at
    /// <c>04.04.00.SqlDataProvider:L15</c>. The column is not nullable and defaults to zero, so zero is
    /// the shipped default and a real value; the wire type is nullable because a projection reports what
    /// it was handed. Interpreting or enforcing a quota is service behaviour. A change here was refused
    /// for a non-super-user (<c>SiteSettings.ascx.vb:L757-L770</c>).
    /// </remarks>
    public int? PageQuota { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of user accounts the portal may contain.
    /// </summary>
    /// <remarks>
    /// Legacy <c>UserQuota</c> (<c>PortalInfo.vb:L181</c>), the <c>txtUserQuota</c> text box, and
    /// <c>Portals.UserQuota</c>, added <c>int NOT NULL ... DEFAULT 0</c> at
    /// <c>04.04.00.SqlDataProvider:L16</c>; the nullability and zero-value reasoning of
    /// <see cref="PageQuota"/> applies. One measurement is worth recording as a live instance of the
    /// compilation asymmetry this migration absorbs: the legacy screen declared its local for this field
    /// as the wide floating-point type, assigned an integer parse into it and passed it to an integer
    /// parameter (<c>SiteSettings.ascx.vb:L719-L722</c>), which compiled only because the legacy web
    /// pages were built with strict type checking disabled. Every such implicit narrowing is made
    /// explicit during translation, and this member is the integer the column actually is.
    /// </remarks>
    public int? UserQuota { get; set; }

    /// <summary>
    /// Gets or sets the name of the payment processor that handles the portal's payments.
    /// </summary>
    /// <remarks>
    /// Legacy <c>PaymentProcessor</c> (<c>PortalInfo.vb:L253</c>), the <c>cboProcessor</c> selector, and
    /// <c>Portals.PaymentProcessor</c>, added <c>nvarchar(50) NULL</c> at
    /// <c>01.00.06.SqlDataProvider:L599</c>. A processor NAME, not a credential, and therefore safe to
    /// report. An empty string here is not merely theoretical: the legacy screen submitted the empty
    /// string explicitly when no processor was selected (<c>SiteSettings.ascx.vb:L778</c>).
    /// </remarks>
    public string? PaymentProcessor { get; set; }

    /// <summary>
    /// Gets or sets the account name the portal presents to its payment processor.
    /// </summary>
    /// <remarks>
    /// Legacy <c>ProcessorUserId</c> (<c>PortalInfo.vb:L269</c>), the <c>txtUserId</c> text box, and
    /// <c>Portals.ProcessorUserId</c>, added <c>nvarchar(50) NULL</c> at
    /// <c>01.00.06.SqlDataProvider:L600</c>. An identifier rather than a secret, so it is reported. It
    /// is the last member of the payment group here; the member that followed it in the legacy update
    /// path is deliberately absent, for the reason recorded immediately below. An empty string may
    /// legitimately mean no value.
    /// </remarks>
    public string? ProcessorUserId { get; set; }

    // MIGRATION: the payment-processor credential is DELIBERATELY ABSENT, and this comment marks the
    //   position it would otherwise occupy -- immediately after the processor account name, which is
    //   where it sits both in the twenty-seven-argument update signature (PortalController.vb:L1568)
    //   and in the schema (the legacy column remains ProcessorPassword nvarchar(50) NULL at
    //   01.00.06.SqlDataProvider:L601).
    //
    //   Its omission is a decision, not an oversight: the column is real and the terminal read view
    //   projects it (04.05.00.SqlDataProvider:L1572), so a mechanical projection of that view WOULD
    //   have carried it. This type is a RESPONSE contract, and a response that echoes a live
    //   third-party credential puts it into every client cache, every browser developer panel and --
    //   because request and response bodies are what structured logging captures -- every log sink that
    //   records a payload. Leaving the member undeclared is a structural guarantee no logging filter can
    //   match. An inbound update contract may still accept the value, since an administrator must be
    //   able to set it; only the outbound projection declines to repeat it.
    //
    //   The legacy application did repeat it: the property (PortalInfo.vb:L261) carried an XML element
    //   decoration and that class was the root of the portal-template serialisation contract
    //   (PortalInfo.vb:L29), so the credential was written in clear into template exports. Per the
    //   domain-logic-preservation clause that legacy defect is annotated rather than repaired -- the
    //   legacy code stays byte-identical. Declining to reproduce a credential leak in a NEW contract is
    //   not a repair of the old one, and the distinction is recorded so a later reader does not
    //   "restore" the member for symmetry with the legacy property list.

    /// <summary>
    /// Gets or sets the number of days of site-activity history the portal retains.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy <c>SiteLogHistory</c> (<c>PortalInfo.vb:L277</c>), the <c>txtSiteLogHistory</c> text box,
    /// and <c>Portals.SiteLogHistory</c>, added <c>int NULL</c> at
    /// <c>01.00.06.SqlDataProvider:L602</c>. The unit is quoted, not inferred:
    /// <c>plSiteLogHistory.Text</c> reads "Site Log History (Days):".
    /// </para>
    /// <para>
    /// SENTINEL NOTE. The legacy screen initialised the value it was about to submit to the literal -1
    /// when the field was blank (<c>SiteSettings.ascx.vb:L724-L727</c>) - the same value the shared null
    /// contract returns as its absent-integer sentinel
    /// (<c>Library/Components/Shared/Null.vb:L41-L45</c>) - while the legacy read path separately
    /// guarded the column against a database null (<c>SiteSettings.ascx.vb:L348-L350</c>), so both
    /// encodings occur in real data. That value may arrive here and is carried as given: a consumer
    /// must not read it as a retention period, and equally must not treat it as authoritative absence,
    /// because this contract converts in neither direction. A change here was refused for a
    /// non-super-user (<c>:L757-L770</c>).
    /// </para>
    /// </remarks>
    public int? SiteLogHistory { get; set; }

    // MIGRATION: the four page-reference members below share one sentinel contract, stated once here.
    //
    //   Each is a nullable Portals column -- SplashTabId int NULL at 03.00.04.SqlDataProvider:L553, and
    //   HomeTabId, LoginTabId and UserTabId int NULL at 02.00.00.SqlDataProvider:L6677, :L6678 and
    //   :L6679 -- so a database null is possible. But the legacy write path did NOT submit a null when
    //   a page was unselected: at SiteSettings.ascx.vb:L738, :L743, :L748 and :L753 the screen
    //   initialised each of the four locals to the shared absent-integer sentinel -1
    //   (Library/Components/Shared/Null.vb:L41-L45) and overwrote it only if the selector had a
    //   selection. Legacy rows therefore carry -1 wherever no page was chosen, alongside genuine nulls
    //   from other write paths.
    //
    //   -1 MUST NOT be read as "absent" by a consumer, and this is not pedantic: the portal key itself
    //   seeds at that very value (01.00.00.SqlDataProvider:L77) and the tab, role and module keys all
    //   seed at zero (:L140, :L115, :L221), so no numeric value in this schema is reserved for absence.
    //   A nullable integer is the honest wire type - it carries both the null the column permits and
    //   the sentinel the legacy application actually wrote. Which of the two a mapper emits is a mapper
    //   decision. This contract converts in neither direction, declares no sentinel constant and offers
    //   no absence test.

    /// <summary>
    /// Gets or sets the identifier of the portal's splash page.
    /// </summary>
    /// <remarks>
    /// Legacy <c>SplashTabId</c> (<c>PortalInfo.vb:L332</c>), the <c>cboSplashTabId</c> selector, and
    /// <c>Portals.SplashTabId</c>, added <c>int NULL</c> at <c>03.00.04.SqlDataProvider:L553</c>. A
    /// legacy row may carry -1 where no page was chosen; see the shared sentinel note above this group
    /// of four members for why that value must not be read as absence.
    /// </remarks>
    public int? SplashTabId { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the portal's home page.
    /// </summary>
    /// <remarks>
    /// Legacy <c>HomeTabId</c> (<c>PortalInfo.vb:L340</c>), the <c>cboHomeTabId</c> selector, and
    /// <c>Portals.HomeTabId</c>, added <c>int NULL</c> at <c>02.00.00.SqlDataProvider:L6677</c>. A
    /// legacy row may carry -1 where no page was chosen; see the shared sentinel note above.
    /// </remarks>
    public int? HomeTabId { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the portal's login page.
    /// </summary>
    /// <remarks>
    /// Legacy <c>LoginTabId</c> (<c>PortalInfo.vb:L348</c>), the <c>cboLoginTabId</c> selector, and
    /// <c>Portals.LoginTabId</c>, added <c>int NULL</c> at <c>02.00.00.SqlDataProvider:L6678</c>. A
    /// legacy row may carry -1 where no page was chosen; see the shared sentinel note above.
    /// </remarks>
    public int? LoginTabId { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the portal's user-account page.
    /// </summary>
    /// <remarks>
    /// Legacy <c>UserTabId</c> (<c>PortalInfo.vb:L356</c>), the <c>cboUserTabId</c> selector, and
    /// <c>Portals.UserTabId</c>, added <c>int NULL</c> at <c>02.00.00.SqlDataProvider:L6679</c>. A
    /// legacy row may carry -1 where no page was chosen; see the shared sentinel note above.
    /// </remarks>
    public int? UserTabId { get; set; }

    /// <summary>
    /// Gets or sets the portal's default language code.
    /// </summary>
    /// <remarks>
    /// Legacy <c>DefaultLanguage</c> (<c>PortalInfo.vb:L364</c>), the <c>cboDefaultLanguage</c>
    /// selector, and <c>Portals.DefaultLanguage</c> - added <c>nvarchar(6) NOT NULL</c> defaulting to
    /// <c>'en-US'</c> at <c>02.02.00.SqlDataProvider:L147</c> and finally widened to
    /// <c>nvarchar(10) NOT NULL</c> at <c>04.03.05.SqlDataProvider:L291</c>, which is the TERMINAL
    /// width and an illustration of why only the end state of the 88-script chain is meaningful. The
    /// value is a culture code carried verbatim, neither parsed nor canonicalised, and it does not imply
    /// that content localisation is available: the legacy resource-file mechanism is not carried
    /// forward, and user-facing wording is authored directly in the client.
    /// </remarks>
    public string? DefaultLanguage { get; set; }

    // MIGRATION: two facts about the time-zone member below, one about its NAME and one about its UNIT.
    //
    //   Name. The schema and the legacy class disagree on the capitalisation of the middle syllable.
    //   The column is spelled with a lower-case middle letter -- added as TimezoneOffset at
    //   02.02.00.SqlDataProvider:L151, re-asserted at 03.01.01.SqlDataProvider:L1122 and projected under
    //   that spelling by the terminal read view at 04.05.00.SqlDataProvider:L1576 -- while the legacy
    //   property (PortalInfo.vb:L372) uses the upper-case form, as does the schema's own
    //   default-constraint name on the very same line as the lower-case column. The legacy code worked
    //   either way because database identifiers are compared case-insensitively; C# is not. This member
    //   uses the compound-word capitalisation, matching the legacy property. The persisted column name
    //   is untouched and the entity configuration binds the schema's spelling.
    //
    //   Unit. The value is an offset in MINUTES, not hours, measured from the option list the legacy
    //   selector was bound to (Website/App_GlobalResources/TimeZones.xml), whose keys are -720 for
    //   twelve hours behind coordinated universal time, -480 for eight hours behind, 0 for coordinated
    //   universal time and 60 for one hour ahead. The column's database default is the bare value -8
    //   (02.02.00.SqlDataProvider:L151), which is not a valid key in that list -- an artefact of an
    //   earlier hours-based encoding. Per the domain-logic-preservation clause that inconsistency is
    //   annotated and NOT repaired: a row still carrying the default matches no entry in the list,
    //   exactly as before this migration.

    /// <summary>
    /// Gets or sets the portal's time-zone offset from coordinated universal time, in MINUTES.
    /// </summary>
    /// <remarks>
    /// Legacy <c>TimeZoneOffset</c> (<c>PortalInfo.vb:L372</c>), the <c>cboTimeZone</c> selector, and
    /// the portal time-zone column, whose terminal state is a non-nullable integer
    /// (<c>03.01.01.SqlDataProvider:L1122</c>). The unit and the spelling are both explained in the
    /// migration note above; a negative value denotes a zone behind coordinated universal time and
    /// carries no other meaning.
    /// </remarks>
    public int? TimeZoneOffset { get; set; }

    /// <summary>
    /// Gets or sets the portal's home directory, the path under which its uploaded content is kept.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Legacy <c>HomeDirectory</c> (<c>PortalInfo.vb:L380</c>), the <c>txtHomeDirectory</c> text box,
    /// and <c>Portals.HomeDirectory</c>, added <c>[varchar](100) NOT NULL</c> with an empty-string
    /// default at <c>02.02.02.SqlDataProvider:L3823</c>.
    /// </para>
    /// <para>
    /// The STORED, relative value and nothing else. The legacy class also exposed a read-only absolute
    /// filesystem path derived from it (<c>PortalInfo.vb:L388</c>); that member is excluded from every
    /// contract here, because it was computed from the excluded application-path module and because an
    /// absolute server path is of no use to a browser client. Resolving this value against the content
    /// root is the infrastructure layer's concern. The column defaults to the empty string and the
    /// legacy null contract also encodes an absent string that way, so an empty value is both expected
    /// and ambiguous between those two origins; it is carried as given.
    /// </para>
    /// </remarks>
    public string? HomeDirectory { get; set; }

    // MIGRATION: the legacy property name is the all-capitals acronym GUID (PortalInfo.vb:L245); this
    //   member uses the compound capitalisation, Guid. That is a real, externally observable contract
    //   change -- a consumer looking for the legacy spelling in a payload will not find it -- and it is
    //   left visible rather than masked by a serialisation-name attribute, so a client model can reuse
    //   this identifier verbatim without a translation table. The same convention applies across the
    //   Portal contract group, matching the sibling PortalAliasDto. Stored column names are untouched.
    //
    //   The legacy property was one of only two on that class excluded from XML serialisation, and it is
    //   excluded from the twenty-seven-argument update path as well, because the database owns the
    //   value: the column is [uniqueidentifier] NOT NULL defaulting to newid() in the baseline
    //   (01.00.00.SqlDataProvider:L93), with its terminal alteration at 03.01.01.SqlDataProvider:L1120
    //   and the generator default re-added at :L1133. It is nevertheless part of THIS contract because
    //   the settings screen displayed it, read-only, in a label at sitesettings.ascx:L69.

    /// <summary>
    /// Gets the globally unique identifier of the portal, which the legacy screen displayed read-only.
    /// </summary>
    /// <remarks>
    /// Legacy <c>GUID</c> (<c>PortalInfo.vb:L245</c>) and <c>Portals.GUID</c>; the rename and the
    /// provenance are explained in the migration note above. The setter exists only so that a mapper
    /// and a deserialiser can populate the member - the value is assigned by the database and is not
    /// administrator-editable, which is why no request contract in this group accepts it. Declared
    /// plain and non-nullable because the column is not nullable, and no emptiness guard is applied:
    /// the generator never produces the all-zero identifier, though a value that travelled the legacy
    /// read path could be that identifier, since it is the shared null contract's absent-identifier
    /// sentinel (<c>Library/Components/Shared/Null.vb:L81-L85</c>). Reporting the value as stored is
    /// schema-faithful; reacting to the sentinel is a service concern.
    /// </remarks>
    public Guid Guid { get; set; }
}
