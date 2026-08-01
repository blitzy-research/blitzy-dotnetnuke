using DnnMigration.Domain.Enums;

namespace DnnMigration.Application.Dtos.Portal;

/// <summary>
/// Wire contract for a single portal's configuration, as the legacy Site Settings screen presented it.
/// </summary>
/// <remarks>
/// <para>
/// Served by <c>PortalsController</c> under <c>/api/v1/portals/{id}/settings</c> and consumed by the
/// Angular <c>features/portal/portal-settings/</c> screen, where the legacy multi-step wizard becomes a
/// single tabbed settings view.
/// </para>
/// <para>
/// A column projection, not a name-and-value collection. Every member below is one column of the
/// <c>Portals</c> table, and there is no portal-level setting record anywhere in this system to model
/// as a keyed collection. Four independent measurements establish that, three negative and one
/// positive. The abstract data surface at
/// <c>Library/Components/Providers/Data/DataProvider.vb</c> declares no portal-setting member among
/// its 269 abstract methods. The concrete provider at
/// <c>Library/Providers/DataProviders/SqlDataProvider/SqlDataProvider.vb</c> invokes no portal-setting
/// procedure among its 245. No portal-settings table is created anywhere in the 88-script schema
/// chain under <c>Website/Providers/DataProviders/SqlDataProvider/</c>; the only five tables whose
/// names end that way are the host-level one, <c>ModuleSettings</c>, <c>TabModuleSettings</c>,
/// <c>ScheduleItemSettings</c> and a transient <c>Tmp_ModuleSettings</c>. And positively, the legacy
/// accessor at <c>Library/Components/Portal/PortalController.vb:L1209-L1211</c> pulled its
/// <c>PortalSettings</c> object straight from the per-request ambient item collection, so that type
/// was request state composed on the fly rather than a persisted aggregate. Module-level and
/// tab-module-level settings genuinely are keyed collections in this migration, backed by real tables;
/// portal-level configuration is not, and is projected column by column here.
/// </para>
/// <para>
/// Inert by design. This type holds values and exposes no behaviour: no validation, no clamping, no
/// normalisation, no computed member, no conversion and no access to any store. The legacy behaviour
/// that surrounded these values lives where the target architecture places it. The two comparisons the
/// legacy screen declared, both of them <c>CompareValidator</c> instances - a date check on the expiry
/// field at <c>Website/admin/Portal/sitesettings.ascx:L433</c> and a currency check on the hosting fee
/// at <c>:L444</c> - belong to <c>Application/Validation/UpdatePortalRequestValidator.cs</c>. The
/// legacy screen declared no required-field check at all, and the disk-space field carried no check
/// whatever, so the validator author must not infer requiredness from this contract's nullability.
/// Translating between this contract and the persisted record belongs to
/// <c>Application/Mapping/PortalMappings.cs</c>, which is hand-written. The rule that locks
/// portal-level banner editing when the mode is host-managed, measured at
/// <c>Website/admin/Portal/SiteSettings.ascx.vb:L295-L296</c>, belongs to <c>PortalService</c> and to
/// the Angular form; no editability flag appears below. The guard that refuses a non-super-user's
/// change to the hosting fee, disk space, quotas, log retention or expiry date, measured at
/// <c>SiteSettings.ascx.vb:L757-L770</c>, likewise belongs to the service.
/// </para>
/// <para>
/// No persisted record type is exposed here, in either direction. Keeping the transported shape
/// distinct from the stored one is exactly what allows the legacy sentinel semantics described below
/// to be honoured at the API edge without contaminating the model behind it. For the same reason no
/// identifier-wrapper value type appears on any member: those wrappers expose their payload through a
/// nested property and carry no serialisation converter, so a client would receive an object where it
/// expects a number or a string. Plain primitives are used throughout.
/// </para>
/// <para>
/// The legacy null contract, and why it is visible on this contract. The shared sentinel helper at
/// <c>Library/Components/Shared/Null.vb</c> encodes absence as an in-band value rather than as a
/// database null: -1 for an integer (L41-L45), the empty string for a string (L71-L75),
/// <c>DateTime.MinValue</c> for a date (L66-L70) and <c>Guid.Empty</c> for a globally unique
/// identifier (L81-L85). Its conversion helpers at L88 and L119 applied that encoding on every read,
/// and its absence test at L208-L237 reports each of those values as absent. Those values are
/// therefore externally observable in legacy data and may legitimately arrive on the members below.
/// This contract transports whichever value it is handed and converts in neither direction; any
/// decision to reconcile a sentinel with a null belongs to <c>Application/Mapping/PortalMappings.cs</c>
/// and is recorded there. Nothing below treats any particular numeric or string value as meaning
/// absence, and no member offers a helper that would.
/// </para>
/// <para>
/// The identifier trap, stated once so no consumer has to rediscover it. The <c>Portals</c> primary
/// key is declared <c>[PortalID] [int] IDENTITY (-1, 1) NOT NULL</c> at
/// <c>Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider:L77</c>, and the
/// installation ships a portal whose key is zero, inserted at
/// <c>01.00.00.SqlDataProvider:L7125</c>. The seed value of that identity column is simultaneously
/// the legacy absent-integer sentinel. Both values are therefore real, addressable portal
/// identifiers, and neither a negative key nor a zero key indicates a missing portal. Sibling tables
/// seed differently again - roles, tabs and modules from zero, users from one - so no single numeric
/// convention for absence exists in this schema at all.
/// </para>
/// <para>
/// Wording. The label and help text on every member below is quoted from
/// <c>Website/admin/Portal/App_LocalResources/SiteSettings.ascx.resx</c>, which is the authoritative
/// record of what the legacy screen displayed, so that the Angular labels stay recognisable to
/// existing administrators.
/// </para>
/// </remarks>
public sealed class PortalSettingsDto
{
    // MIGRATION: this contract deliberately carries no keyed collection of settings, and no member of
    //   the legacy per-request PortalSettings composite. The legacy type of that name was assembled
    //   per request and pulled from ambient request state (PortalController.vb:L1209-L1211); its
    //   mutable current-page member, settable at PortalSettings.vb:L398 and initialised at L550,
    //   is request state and has no place on a wire contract. Of that composite's members, only the
    //   handful that describe the request survive, as an immutable per-request context abstraction
    //   declared in the Domain layer and consumed by the Application services. Everything that is
    //   genuinely a Portals column is projected here instead, as its own strongly typed named member.
    //   Nothing on this contract is derived from ambient request state, and nothing here is a
    //   host-level setting: the only host-prefixed members below are the two real Portals columns
    //   that carry that prefix in the schema.

    // MIGRATION: the legacy PortalInfo class carried 39 properties; this configuration projection
    //   carries 27. The difference is measured, not arbitrary. The legacy update path -- the
    //   twenty-seven-argument PortalController.UpdatePortalInfo at PortalController.vb:L1568, a void
    //   Sub -- excludes exactly twelve of those 39, and every one of the twelve is excluded here too,
    //   for a reason established against the terminal schema rather than assumed:
    //
    //     Email, AdministratorRoleName, RegisteredRoleName, SuperTabId
    //         Not Portals columns. They reach the legacy read path only because that path is the view
    //         vw_Portals, whose terminal definition is at
    //         Website/Providers/DataProviders/SqlDataProvider/04.05.00.SqlDataProvider:L1530-L1587
    //         (an earlier definition exists at 04.04.00.SqlDataProvider:L26), and which the read
    //         procedures select from wholesale -- GetPortal at 04.04.00.SqlDataProvider:L199 and
    //         GetPortals at :L230. The view closes with
    //         "FROM ...Portals AS P LEFT OUTER JOIN ...Users AS U ON P.AdministratorId = U.UserID"
    //         (L1586-L1587), so the address it projects at L1574 is the portal ADMINISTRATOR's, joined
    //         from the user table. Two independent case-insensitive searches across all 88 schema
    //         scripts -- one for an add-or-alter of such a column on Portals, one listing the columns
    //         of every Portals create-table block -- both return nothing, confirming Portals has no
    //         such column. This is also why the legacy property at PortalInfo.vb:L285 carries no
    //         validator, no length limit and no requiredness, in contrast to the user-side property at
    //         UserInfo.vb:L121-L123. The three remaining members here are view sub-selects: the root
    //         host tab at L1583, identical for every portal, and two role-name lookups at L1584 and
    //         L1585. All four belong to PortalDetailDto as read-only view-derived members.
    //
    //     Users, Pages, Version, HomeDirectoryMapPath
    //         Absent from vw_Portals entirely, which independently confirms none of them is persisted
    //         portal state. The only 8-character string column of that third name in the whole chain
    //         is at 02.00.00.SqlDataProvider:L5144, inside the DesktopModules create-table declared at
    //         L5140 -- it belongs to that table, not to Portals. The fourth is a filesystem path the
    //         legacy class computed read-only at PortalInfo.vb:L388 from the excluded application-path
    //         utility module, whose replacement is bound configuration and environment paths rather
    //         than a wire member.
    //
    //     AdministratorRoleId, RegisteredRoleId, AdminTabId
    //         Real Portals columns -- the first two declared in the baseline at
    //         01.00.00.SqlDataProvider:L91 and :L92, the third added at
    //         02.02.00.SqlDataProvider:L232 -- and all three are projected by vw_Portals at L1550,
    //         L1551 and L1577. They are nevertheless excluded here because neither of the two
    //         authorities for this contract admits them: no control for any of them appears on
    //         Website/admin/Portal/sitesettings.ascx, and none of them appears among the
    //         twenty-seven arguments. Neither the settings screen nor the legacy update path treated
    //         them as administrator-editable configuration, so reproducing them on a settings
    //         projection would invent an affordance the legacy application did not offer. They belong
    //         to PortalDetailDto.
    //
    //     GUID
    //         Excluded from the update path only, because the database owns it. It IS displayed by
    //         the settings screen and is therefore present below; see the note on that member.
    //
    //   The XML serialisation decoration that the legacy class carried on almost every one of those
    //   39 properties, including the class-level element name at PortalInfo.vb:L29 and the namespace
    //   imported at :L26, is dropped rather than translated. Serialisation happens at the API
    //   boundary only, and this contract states the wire shape through its member names alone: no
    //   serialisation attribute, no validation attribute and no persistence attribute appears below.

    // MIGRATION: fields the legacy Site Settings screen offered that are deliberately absent from
    //   every Portal contract in this migration, recorded here so their absence reads as a decision
    //   rather than an omission. The screen mixed portal configuration with settings belonging to
    //   subsystems this migration excludes: a search-provider selector, an inline-editing toggle, four
    //   transport-security fields, a stylesheet field, a site-map field, a search-engine submission
    //   field and three control-panel option lists. Independently of the exclusion list, not one of
    //   them appears among the twenty-seven arguments either, which confirms none of them is a Portals
    //   column reachable by this work. The same screen also hosts four skin and container pickers and
    //   a desktop-module assignment list; skinning is excluded, and portal-to-desktop-module
    //   assignment is a separate join table rather than a Portals column.

    /// <summary>
    /// Gets or sets the identifier of the portal this configuration describes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Corresponds to the legacy <c>PortalID</c> property (<c>PortalInfo.vb:L85</c>), to the first of
    /// the twenty-seven arguments, and to the <c>Portals.PortalID</c> column, declared
    /// <c>[PortalID] [int] IDENTITY (-1, 1) NOT NULL</c> at
    /// <c>Website/Providers/DataProviders/SqlDataProvider/01.00.00.SqlDataProvider:L77</c>. The column
    /// is not nullable and a settings response always describes an existing portal, so this member is
    /// a plain integer.
    /// </para>
    /// <para>
    /// Every value this member can hold identifies a portal. The identity seed is negative and the
    /// installation ships a portal at zero (<c>01.00.00.SqlDataProvider:L7125</c>), so a consumer must
    /// not infer absence from any particular value; the type-level remarks set this trap in full.
    /// </para>
    /// </remarks>
    public int PortalId { get; set; }

    /// <summary>
    /// Gets or sets the portal's display name, which the legacy screen labelled "Title:".
    /// </summary>
    /// <remarks>
    /// <para>
    /// Corresponds to the legacy <c>PortalName</c> property (<c>PortalInfo.vb:L93</c>), to the
    /// <c>txtPortalName</c> text box on <c>sitesettings.ascx</c>, and to the <c>Portals.PortalName</c>
    /// column, declared <c>[nvarchar] (128) NOT NULL</c> at
    /// <c>01.00.00.SqlDataProvider:L79</c>. The resource entry <c>plPortalName.Help</c> reads "This is
    /// the Title for your portal. The text you enter will show up in the Title Bar."
    /// </para>
    /// <para>
    /// Declared nullable even though the column is not, because a projection may be handed a value it
    /// did not receive and this contract invents nothing. An empty string may legitimately appear here
    /// where a modern reader would expect no value at all, because the legacy null contract encodes an
    /// absent string as the empty string; the two are indistinguishable in legacy data.
    /// </para>
    /// </remarks>
    public string? PortalName { get; set; }

    /// <summary>
    /// Gets or sets the portal's descriptive text.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Corresponds to the legacy <c>Description</c> property (<c>PortalInfo.vb:L221</c>), to the
    /// <c>txtDescription</c> text box, and to the <c>Portals.Description</c> column, added
    /// <c>Description nvarchar(500) NULL</c> at
    /// <c>01.00.02.SqlDataProvider:L884</c>. The resource entry <c>plDescription.Help</c> reads "Enter
    /// a description about your site here."
    /// </para>
    /// <para>
    /// An empty string may legitimately appear here where a modern reader would expect no value,
    /// because the legacy null contract encodes an absent string as the empty string; the two are
    /// indistinguishable in legacy data.
    /// </para>
    /// </remarks>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the portal's search keywords, separated by commas.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Corresponds to the legacy <c>KeyWords</c> property (<c>PortalInfo.vb:L229</c>), to the
    /// <c>txtKeyWords</c> text box, and to the <c>Portals.KeyWords</c> column, added
    /// <c>KeyWords nvarchar(500) NULL</c> at <c>01.00.02.SqlDataProvider:L885</c>. The legacy
    /// capitalisation of the second syllable is kept deliberately: it is the spelling of both the
    /// legacy property and the column, so a client model can reuse this identifier verbatim. The
    /// resource entry <c>plKeyWords.Help</c> reads "Enter some keywords for your site (separated by
    /// commas). These keywords are used by search engines to help index your site."
    /// </para>
    /// <para>
    /// An empty string may legitimately appear here where a modern reader would expect no value,
    /// because the legacy null contract encodes an absent string as the empty string; the two are
    /// indistinguishable in legacy data. Nothing here parses, splits or trims the list.
    /// </para>
    /// </remarks>
    public string? KeyWords { get; set; }

    /// <summary>
    /// Gets or sets the footer text, which the legacy screen labelled "Copyright:".
    /// </summary>
    /// <remarks>
    /// <para>
    /// Corresponds to the legacy <c>FooterText</c> property (<c>PortalInfo.vb:L109</c>), to the
    /// <c>txtFooterText</c> text box, and to the <c>Portals.FooterText</c> column, declared
    /// <c>[nvarchar] (100) NULL</c> at <c>01.00.00.SqlDataProvider:L82</c>. The resource entry
    /// <c>plFooterText.Help</c> reads "If supported by the skin this Copyright text is displayed on
    /// your site." Whether it is rendered was a skinning concern, and skinning is excluded from this
    /// migration; the value itself is portal configuration and is carried.
    /// </para>
    /// <para>
    /// An empty string may legitimately appear here where a modern reader would expect no value,
    /// because the legacy null contract encodes an absent string as the empty string; the two are
    /// indistinguishable in legacy data.
    /// </para>
    /// </remarks>
    public string? FooterText { get; set; }

    // MIGRATION: the two file members below are TRANSFORMED ON READ, and the asymmetry is a genuine
    //   round-trip hazard that this contract cannot resolve and must therefore document.
    //
    //   The terminal read path for a portal is not the Portals table; it is the view vw_Portals, which
    //   both read procedures select from wholesale (GetPortal at 04.04.00.SqlDataProvider:L199,
    //   GetPortals at :L230). The terminal view definition at
    //   Website/Providers/DataProviders/SqlDataProvider/04.05.00.SqlDataProvider:L1530-L1587 rewrites
    //   the logo column at L1535-L1544:
    //
    //       CASE WHEN LEFT(LOWER(LogoFile), 6) = 'fileid'
    //            THEN (SELECT Folder + FileName FROM ...Files
    //                  WHERE 'fileid=' + convert(varchar, ...Files.FileID) = LogoFile)
    //            ELSE LogoFile END AS LogoFile
    //
    //   and applies an identical rewrite to the background column at L1559-L1568.
    //
    //   So the STORED value may be a file-identifier token, while the value READ back -- and therefore
    //   the value this contract carries on a response -- is the resolved folder-and-filename path that
    //   the token pointed at. Echoing a value read from this contract straight into an update
    //   overwrites the token with a path and permanently severs the link to the file record. A
    //   consumer that means to leave a file reference untouched must omit the member from its update
    //   rather than round-trip it.
    //
    //   That hazard is inherited, not introduced: the legacy screen itself assigned the picker's
    //   resolved value back into the update at Website/admin/Portal/SiteSettings.ascx.vb:L698-L699 and
    //   compared it against the read value at :L701. Per the domain-logic-preservation clause the
    //   defect is annotated here and NOT repaired, because repairing it would change behaviour.
    //
    //   No token resolution is implemented on this contract. Resolving a token is repository and
    //   mapper territory, and the legacy file-system subtree it would reach into is excluded from this
    //   migration. A dedicated file-reference contract was considered during planning and rejected:
    //   it would reach into that excluded subtree, it would add a seventh type to a folder fixed at
    //   six, and the asymmetry is fully expressible as a nullable string plus this note.

    /// <summary>
    /// Gets or sets the portal logo image reference, which the legacy screen labelled "Logo:".
    /// </summary>
    /// <remarks>
    /// <para>
    /// Corresponds to the legacy <c>LogoFile</c> property (<c>PortalInfo.vb:L101</c>), to the
    /// <c>ctlLogo</c> picker control at <c>sitesettings.ascx:L140</c>, and to the
    /// <c>Portals.LogoFile</c> column, declared <c>[nvarchar] (50) NULL</c> at
    /// <c>01.00.00.SqlDataProvider:L81</c>. The picker is one of the controls whose identifier does not
    /// share a prefix with the screen's text boxes and selectors, which is why a prefix-filtered
    /// inventory of that markup omits it; the twenty-seven-argument update path names it as its third
    /// argument, and the resource entry <c>plLogo.Text</c> reads "Logo:", so it is unambiguously part
    /// of this screen. The resource entry <c>plLogo.Help</c> reads "Depending on the skin chosen, this
    /// image will appear in the top left corner of the page."
    /// </para>
    /// <para>
    /// The value read here is the RESOLVED folder-and-filename path whenever the stored value was a
    /// file-identifier token; see the migration note above this member before writing it back. An
    /// empty string may also legitimately appear where a modern reader would expect no value, because
    /// the legacy null contract encodes an absent string as the empty string.
    /// </para>
    /// </remarks>
    public string? LogoFile { get; set; }

    /// <summary>
    /// Gets or sets the page background image reference, which the legacy screen labelled "Body
    /// Background:".
    /// </summary>
    /// <remarks>
    /// <para>
    /// Corresponds to the legacy <c>BackgroundFile</c> property (<c>PortalInfo.vb:L237</c>), to the
    /// <c>ctlBackground</c> picker control at <c>sitesettings.ascx:L150</c>, and to the
    /// <c>Portals.BackgroundFile</c> column, added <c>BackgroundFile nvarchar(50) NULL</c> at
    /// <c>01.00.02.SqlDataProvider:L886</c>. The resource entry <c>plBackground.Help</c> reads
    /// "Depending on the skin, if selected, an image will display in the background of all pages."
    /// </para>
    /// <para>
    /// The value read here is the RESOLVED folder-and-filename path whenever the stored value was a
    /// file-identifier token; the migration note above <see cref="LogoFile"/> applies verbatim to this
    /// member, whose view rewrite is character-for-character identical. An empty string may also
    /// legitimately appear where a modern reader would expect no value, because the legacy null
    /// contract encodes an absent string as the empty string.
    /// </para>
    /// </remarks>
    public string? BackgroundFile { get; set; }

    /// <summary>
    /// Gets or sets the date on which the portal's hosting contract expires.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Corresponds to the legacy <c>ExpiryDate</c> property (<c>PortalInfo.vb:L117</c>, typed as a VB
    /// <c>Date</c>), to the <c>txtExpiryDate</c> text box with its adjacent calendar link, and to the
    /// <c>Portals.ExpiryDate</c> column, declared <c>[datetime] NULL</c> at
    /// <c>01.00.00.SqlDataProvider:L83</c>. The resource entry <c>plExpiryDate.Help</c> reads "The
    /// Expiry Date is the date that the Hosting Contract for the portal expires." The legacy screen
    /// declared a date-type comparison on the field at <c>sitesettings.ascx:L433</c>, whose message
    /// reads "Invalid expiry date!"; reproducing that comparison belongs to the update-request
    /// validator.
    /// </para>
    /// <para>
    /// Sentinel note, and it matters here more than anywhere else on this contract. The legacy code
    /// did not represent an unset expiry as a database null: the screen initialised the value it was
    /// about to submit to the absent-date sentinel at
    /// <c>Website/admin/Portal/SiteSettings.ascx.vb:L733</c>, and that sentinel is
    /// <c>DateTime.MinValue</c> - the first day of year one - per
    /// <c>Library/Components/Shared/Null.vb:L66-L70</c>. Worse for a naive reader, the legacy absence
    /// test compares only the DATE PART: at <c>Null.vb:L222-L224</c> it converts the value and then
    /// compares <c>objDate.Date</c> against the sentinel's date, so any instant whose date part is the
    /// first day of year one was treated as absent regardless of its time component. A value equal to
    /// that sentinel may therefore arrive on this member, and this contract does not convert it to a
    /// null; whether the mapper does is a mapper decision and is recorded there.
    /// </para>
    /// </remarks>
    public DateTime? ExpiryDate { get; set; }

    /// <summary>
    /// Gets or sets the mode by which the portal admits new user accounts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Corresponds to the legacy <c>UserRegistration</c> property (<c>PortalInfo.vb:L125</c>), which
    /// exposed a bare integer discriminator, and to the <c>optUserRegistration</c> option list at
    /// <c>sitesettings.ascx:L228-L234</c>, whose four items carry the values zero through three under
    /// the labels "None", "Private", "Public" and "Verified". The resource entry
    /// <c>plUserRegistration.Help</c> reads "The type of user registration allowed for this site".
    /// </para>
    /// <para>
    /// Typed as <see cref="UserRegistrationMode"/> rather than as a bare integer, so the persisted
    /// discriminator is named at the boundary instead of transported as a magic number. The enumeration
    /// serialises as its numeric value by default, which is byte-identical to the stored column and to
    /// what the legacy option list posted, so naming it costs no change in the wire shape and no
    /// serialisation attribute is added to force one.
    /// </para>
    /// <para>
    /// Not nullable, because the backing column is not. Its terminal state is
    /// <c>ALTER COLUMN [UserRegistration] [int] NOT NULL</c> at
    /// <c>03.01.01.SqlDataProvider:L1116</c>, with a database default of zero added at <c>:L1125</c>.
    /// The zero member is consequently a real, chosen mode rather than a stand-in for a missing value.
    /// </para>
    /// </remarks>
    public UserRegistrationMode UserRegistration { get; set; }

    /// <summary>
    /// Gets or sets the portal's banner-advertising mode.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Corresponds to the legacy <c>BannerAdvertising</c> property (<c>PortalInfo.vb:L133</c>), which
    /// exposed a bare integer discriminator, and to the <c>optBanners</c> option list at
    /// <c>sitesettings.ascx:L121-L126</c>, whose three items carry the values zero, one and two under
    /// the labels "None", "Site" and "Host". The resource entry <c>plBanners.Help</c> reads "Indicate
    /// the type of Banner Advertising you wish to display on your site."
    /// </para>
    /// <para>
    /// Typed as <see cref="BannerAdvertisingMode"/> for the same reason, and with the same absence of
    /// any serialisation attribute, as <see cref="UserRegistration"/> above.
    /// </para>
    /// <para>
    /// Not nullable, because the backing column is not: its terminal state is
    /// <c>ALTER COLUMN [BannerAdvertising] [int] NOT NULL</c> at
    /// <c>03.01.01.SqlDataProvider:L1117</c>, defaulting to zero at <c>:L1127</c>.
    /// </para>
    /// <para>
    /// One live legacy rule keys off the host-managed member, and it is deliberately NOT implemented
    /// here. At <c>Website/admin/Portal/SiteSettings.ascx.vb:L295-L296</c> the screen disabled
    /// portal-level banner editing and revealed an explanatory label - "Banner option was set by the
    /// hostingprovider, and cannot be changed" - whenever the stored mode was the host-managed one,
    /// and it took that branch only for a caller who was not a super user. That is service and form
    /// behaviour; this contract reports the mode and offers no editability flag.
    /// </para>
    /// </remarks>
    public BannerAdvertisingMode BannerAdvertising { get; set; }

    /// <summary>
    /// Gets or sets the three-letter currency code used for the portal's monetary values.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Corresponds to the legacy <c>Currency</c> property (<c>PortalInfo.vb:L149</c>), to the
    /// <c>cboCurrency</c> selector, and to the <c>Portals.Currency</c> column, declared
    /// <c>[char] (3) NULL</c> at <c>01.00.00.SqlDataProvider:L88</c>. The resource entry
    /// <c>plCurrency.Help</c> reads "The Currency used on the site." The stored width is three
    /// characters; that limit is documented here for the update-request validator to enforce and is
    /// deliberately not declared as an attribute on this contract.
    /// </para>
    /// <para>
    /// Because the column is a fixed-width character type, a value read from a legacy row may carry
    /// trailing padding. This contract neither trims nor pads. An empty string may also legitimately
    /// appear where a modern reader would expect no value, because the legacy null contract encodes an
    /// absent string as the empty string.
    /// </para>
    /// </remarks>
    public string? Currency { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the user account that administers the portal.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Corresponds to the legacy <c>AdministratorId</c> property (<c>PortalInfo.vb:L141</c>), to the
    /// <c>cboAdministratorId</c> selector, and to the <c>Portals.AdministratorId</c> column, declared
    /// <c>[int] NULL</c> at <c>01.00.00.SqlDataProvider:L86</c>. It is a foreign key to the user
    /// record: the terminal read view joins on it, <c>ON P.AdministratorId = U.UserID</c>
    /// (<c>04.05.00.SqlDataProvider:L1587</c>). The resource entry <c>plAdministratorId.Text</c> reads
    /// "Administrator:".
    /// </para>
    /// <para>
    /// Nullable, matching the column. Every value it can hold is a meaningful user identifier: the
    /// user identity column seeds from one (<c>01.00.00.SqlDataProvider:L98</c>), and the legacy
    /// absent-integer sentinel is a negative value, so a consumer must not infer absence from any
    /// particular number.
    /// </para>
    /// </remarks>
    public int? AdministratorId { get; set; }

    // MIGRATION: the hosting fee carries a genuine THREE-WAY type conflict, and the terminal schema
    //   decides it. The three disagreeing sources are:
    //
    //     1. the legacy class     -- PortalInfo.vb:L157 declares the property As Single;
    //     2. the legacy setter    -- the twenty-seven-argument PortalController.UpdatePortalInfo at
    //                                PortalController.vb:L1568 declares the argument As Double,
    //                                because the screen parsed its text box into that wider type
    //                                first (SiteSettings.ascx.vb:L704-L707);
    //     3. the terminal schema  -- ALTER COLUMN [HostFee] [money] NOT NULL at
    //                                03.01.01.SqlDataProvider:L1118, with a database default of zero
    //                                added at :L1129.
    //
    //   The schema is authoritative, because the schema is immutable in this migration and every
    //   mapping binds to it. A money column is an exact scaled type, so the wire type is a nullable
    //   decimal: choosing the wider binary floating-point type because the legacy setter used it, or
    //   the narrower one because the legacy property used it, would introduce representation error
    //   into a monetary value that the database stores exactly.
    //
    //   Two independent corroborations that money is right. The schema chain migrated this column from
    //   a 10-character string to money by an explicit conversion, visible at
    //   01.00.05.SqlDataProvider:L1412, and every stored-procedure parameter for it in the terminal
    //   scripts is declared money (or money defaulted to zero); the earlier string form survives only
    //   in the pre-migration scripts. The legacy screen also validated the field as a currency value,
    //   at sitesettings.ascx:L444-L446, whose message reads "Invalid fee, needs to be a currency
    //   value!".
    //
    //   No clamp is applied. The legacy code clamped fees to a floor with an inline conditional at
    //   PortalController.vb:L395 and :L398, but those two statements act on role fees inside a
    //   role-creation helper, not on this column -- and either way clamping is service behaviour. A
    //   data carrier does not arbitrate values.

    /// <summary>
    /// Gets or sets the monthly monetary charge for hosting the portal, expressed in the currency named
    /// by <see cref="Currency"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Corresponds to the legacy <c>HostFee</c> property (<c>PortalInfo.vb:L157</c>), to the
    /// <c>txtHostFee</c> text box, and to the <c>Portals.HostFee</c> column. The resource entry
    /// <c>plHostFee.Text</c> reads "Hosting Fee:" and <c>plHostFee.Help</c> reads "The Hosting Fee is
    /// the monthly charge for hosting this site.", which is where the monthly period and the monetary
    /// unit come from - neither is inferred.
    /// </para>
    /// <para>
    /// Typed as a nullable decimal to match the exact scaled money column; see the migration note above
    /// this member for the full three-way resolution. The column itself is not nullable and defaults
    /// to zero, so zero is a real, chosen fee rather than a marker for a missing one; the wire type is
    /// nullable because a projection reports only what it was handed and this contract invents no
    /// value.
    /// </para>
    /// <para>
    /// A change to this member was privileged in the legacy application: the screen refused a
    /// non-super-user's alteration of it (<c>SiteSettings.ascx.vb:L757-L770</c>). That authorisation
    /// rule belongs to the service and to the authorisation policies, not to this contract.
    /// </para>
    /// </remarks>
    public decimal? HostFee { get; set; }

    // MIGRATION: the disk-space allowance carries the same THREE-WAY conflict shape as the hosting fee
    //   above, but it resolves to a DIFFERENT type, and the divergence from the folder-level
    //   requirements is reported here rather than quietly absorbed.
    //
    //     1. the legacy class     -- PortalInfo.vb:L165 declares the property As Integer;
    //     2. the legacy setter    -- the twenty-seven-argument setter at PortalController.vb:L1568
    //                                declares the argument As Double, because the screen parsed its
    //                                text box into that wider type first
    //                                (SiteSettings.ascx.vb:L709-L712);
    //     3. the terminal schema  -- ALTER COLUMN [HostSpace] [int] NOT NULL at
    //                                03.01.01.SqlDataProvider:L1119, with a database default of zero
    //                                added at :L1131.
    //
    //   REPORTED DIVERGENCE: the folder-level requirements table asserted that this column is a money
    //   type, exactly like the hosting fee, and predicted a nullable exact-scaled wire type for it. The
    //   measured schema does not support that premise. The terminal alteration is an integer column,
    //   sitting on the line immediately after the money alteration for the fee, and every
    //   stored-procedure parameter for it in the terminal scripts is declared int -- twelve as a plain
    //   integer and fifteen as an integer defaulted to null; not one is declared money anywhere in the
    //   88-script chain. The rebuild at 01.00.05.SqlDataProvider:L1412 corroborates it a third way:
    //   that statement wraps the fee in an explicit conversion to money while carrying this column
    //   across UNCONVERTED, precisely because it was already an integer.
    //
    //   Applying the requirements' own stated tie-break -- the entity and schema type wins, because the
    //   schema is immutable -- therefore yields a nullable integer here and a nullable decimal for the
    //   fee. The requirements' fee prediction is confirmed; their disk-space prediction is corrected,
    //   and the correction is stated plainly rather than presented as agreement.

    /// <summary>
    /// Gets or sets the disk-space allowance for the portal, in megabytes, where zero denotes an
    /// unlimited allowance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Corresponds to the legacy <c>HostSpace</c> property (<c>PortalInfo.vb:L165</c>), to the
    /// <c>txtHostSpace</c> text box, and to the <c>Portals.HostSpace</c> column. The unit and the
    /// meaning of zero are both quoted, not inferred: the resource entry <c>plHostSpace.Text</c> reads
    /// "Disk Space:" and <c>plHostSpace.Help</c> reads "The amount of Disk Space in MB allowed for this
    /// site (enter 0 for unlimited space)."
    /// </para>
    /// <para>
    /// That makes zero a load-bearing value on this member and the clearest illustration on this
    /// contract of why absence must never be inferred from a number: on this member zero means the
    /// opposite of "nothing allowed". A consumer must render and transmit it as given.
    /// </para>
    /// <para>
    /// Typed as a nullable integer to match the measured column; see the migration note above this
    /// member, which also records the divergence from the folder-level requirements. The legacy screen
    /// declared no validator whatever on this field, so the absence of one downstream is faithful
    /// rather than an oversight. As with the hosting fee, a change to this member was refused for a
    /// non-super-user at <c>SiteSettings.ascx.vb:L757-L770</c>, which is a service concern.
    /// </para>
    /// </remarks>
    public int? HostSpace { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of pages the portal may contain.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Corresponds to the legacy <c>PageQuota</c> property (<c>PortalInfo.vb:L173</c>), to the
    /// <c>txtPageQuota</c> text box, and to the <c>Portals.PageQuota</c> column, added
    /// <c>PageQuota int NOT NULL CONSTRAINT ... DEFAULT 0</c> at
    /// <c>04.04.00.SqlDataProvider:L15</c>. The resource entry <c>plPageQuota.Help</c> reads "You can
    /// specify a maximum number of pages per portal."
    /// </para>
    /// <para>
    /// The column is not nullable and defaults to zero; the wire type is nullable because a projection
    /// reports only what it was handed. Zero is the shipped default and is a real value, not a marker
    /// for a missing one. Interpreting a quota is service behaviour and no interpretation is applied
    /// here. A change to this member was refused for a non-super-user at
    /// <c>SiteSettings.ascx.vb:L757-L770</c>.
    /// </para>
    /// </remarks>
    public int? PageQuota { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of user accounts the portal may contain.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Corresponds to the legacy <c>UserQuota</c> property (<c>PortalInfo.vb:L181</c>), to the
    /// <c>txtUserQuota</c> text box, and to the <c>Portals.UserQuota</c> column, added
    /// <c>UserQuota int NOT NULL CONSTRAINT ... DEFAULT 0</c> at
    /// <c>04.04.00.SqlDataProvider:L16</c>. The resource entry <c>plUserQuota.Help</c> reads "You can
    /// specify a maximum number of users per portal."
    /// </para>
    /// <para>
    /// The same nullability and zero-value reasoning as <see cref="PageQuota"/> applies. One
    /// measurement is worth recording because it is a live instance of the compilation asymmetry this
    /// migration has to absorb: the legacy screen declared its local for this field as the wide
    /// floating-point type, assigned an integer parse into it and then passed it to an integer
    /// parameter, at <c>SiteSettings.ascx.vb:L719-L722</c>. That compiled only because the legacy web
    /// pages were built with strict type checking disabled. Every such implicit narrowing is made
    /// explicit during translation, and this member is declared as the integer the column actually is.
    /// </para>
    /// </remarks>
    public int? UserQuota { get; set; }

    /// <summary>
    /// Gets or sets the name of the payment processor that handles the portal's payments.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Corresponds to the legacy <c>PaymentProcessor</c> property (<c>PortalInfo.vb:L253</c>), to the
    /// <c>cboProcessor</c> selector, and to the <c>Portals.PaymentProcessor</c> column, added
    /// <c>PaymentProcessor nvarchar(50) NULL</c> at <c>01.00.06.SqlDataProvider:L599</c>. The resource
    /// entry <c>plProcessor.Help</c> reads "The Payment Processor used to handle payments on the site."
    /// </para>
    /// <para>
    /// A processor NAME, not a credential, and therefore safe to report. An empty string may
    /// legitimately appear here where a modern reader would expect no value, because the legacy null
    /// contract encodes an absent string as the empty string - and in this case that is not merely
    /// theoretical: the legacy screen submitted the empty string explicitly when no processor was
    /// selected, through an inline conditional at <c>SiteSettings.ascx.vb:L778</c>.
    /// </para>
    /// </remarks>
    public string? PaymentProcessor { get; set; }

    /// <summary>
    /// Gets or sets the account name the portal presents to its payment processor.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Corresponds to the legacy <c>ProcessorUserId</c> property (<c>PortalInfo.vb:L269</c>), to the
    /// <c>txtUserId</c> text box, and to the <c>Portals.ProcessorUserId</c> column, added
    /// <c>ProcessorUserId nvarchar(50) NULL</c> at <c>01.00.06.SqlDataProvider:L600</c>. The resource
    /// entry <c>plUserId.Text</c> reads "Processor UserId:" and <c>plUserId.Help</c> reads "The UserId
    /// for the Payment Processor."
    /// </para>
    /// <para>
    /// An identifier rather than a secret, so it is reported. It is the last member of the payment
    /// group on this contract; the member that followed it in the legacy update path is deliberately
    /// absent, for the reason recorded immediately below. An empty string may legitimately appear here
    /// where a modern reader would expect no value, because the legacy null contract encodes an absent
    /// string as the empty string.
    /// </para>
    /// </remarks>
    public string? ProcessorUserId { get; set; }

    // MIGRATION: the payment-processor credential is DELIBERATELY ABSENT from this contract, and this
    //   comment marks the position it would otherwise have occupied -- immediately after the processor
    //   account name, which is where it sits in the twenty-seven-argument update signature at
    //   PortalController.vb:L1568 and in the schema, added as ProcessorPassword nvarchar(50) NULL at
    //   01.00.06.SqlDataProvider:L601.
    //
    //   Its omission is a decision, not an oversight. The column is real and the terminal read view
    //   projects it (04.05.00.SqlDataProvider:L1572), so a mechanical projection of that view WOULD
    //   have carried it. This type is a RESPONSE contract, and a response that echoes a live
    //   third-party credential puts that credential into every client cache, every browser developer
    //   panel and -- because request and response bodies are what structured logging captures -- every
    //   log sink that ever records a payload. Leaving the member undeclared is the structural
    //   guarantee that it cannot reach any of them, which no logging filter can promise as reliably.
    //   The inbound update contract may still accept the value, since an administrator must be able to
    //   set it; only the outbound projection declines to repeat it.
    //
    //   The legacy application did repeat it. The property at PortalInfo.vb:L261 carried an XML element
    //   decoration, <XmlElement("processorpassword")>, and that class was the root of the portal
    //   template serialisation contract (its class-level decoration is at PortalInfo.vb:L29), so the
    //   credential was written in clear into portal template exports. That is a defect in the legacy
    //   exporter. Per the domain-logic-preservation clause it is annotated rather than repaired: no
    //   change is made to the legacy code, which stays byte-identical. Declining to reproduce a
    //   credential leak in a NEW contract is not a repair of the old one, and the distinction is
    //   recorded here so a later reader does not "restore" the member for symmetry with the legacy
    //   property list.

    /// <summary>
    /// Gets or sets the number of days of site-activity history the portal retains.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Corresponds to the legacy <c>SiteLogHistory</c> property (<c>PortalInfo.vb:L277</c>), to the
    /// <c>txtSiteLogHistory</c> text box, and to the <c>Portals.SiteLogHistory</c> column, added
    /// <c>SiteLogHistory int NULL</c> at <c>01.00.06.SqlDataProvider:L602</c>. The unit is quoted, not
    /// inferred: the resource entry <c>plSiteLogHistory.Text</c> reads "Site Log History (Days):" and
    /// <c>plSiteLogHistory.Help</c> reads "The number of days of site activity that is kept for this
    /// site."
    /// </para>
    /// <para>
    /// Sentinel note. The legacy screen initialised the value it was about to submit to the literal
    /// -1 when the field was blank, at <c>SiteSettings.ascx.vb:L724-L727</c> - the same value the
    /// shared null contract returns as its absent-integer sentinel
    /// (<c>Library/Components/Shared/Null.vb:L41-L45</c>), and which its absence test reports as absent
    /// for an integer (<c>Null.vb:L210-L211</c>). That value may therefore arrive on this member and is
    /// carried as given; a consumer must not read it as a retention period, and equally must not treat
    /// it as authoritative absence, since this contract performs no conversion in either direction.
    /// The legacy read path also guarded this column against a database null explicitly, at
    /// <c>SiteSettings.ascx.vb:L348-L350</c>, so both encodings occur in real data. A change to this
    /// member was refused for a non-super-user at <c>SiteSettings.ascx.vb:L757-L770</c>.
    /// </para>
    /// </remarks>
    public int? SiteLogHistory { get; set; }

    // MIGRATION: the four page-reference members below share one sentinel contract, stated once here
    //   and cross-referenced from each of them.
    //
    //   Each is a nullable Portals column -- SplashTabId int NULL at 03.00.04.SqlDataProvider:L553,
    //   and HomeTabId, LoginTabId and UserTabId int NULL at 02.00.00.SqlDataProvider:L6677, :L6678 and
    //   :L6679 -- so a database null is possible. But the legacy write path did NOT submit a null when
    //   a page was unselected. At Website/admin/Portal/SiteSettings.ascx.vb:L738, :L743, :L748 and
    //   :L753 the screen initialised each of the four locals it was about to submit to the shared
    //   absent-integer sentinel from Library/Components/Shared/Null.vb:L41-L45, whose value is -1, and
    //   overwrote it only if the corresponding selector had a selection. Legacy rows therefore carry
    //   -1 in these columns wherever no page was chosen, alongside genuine nulls from other write
    //   paths.
    //
    //   -1 MUST NOT be interpreted as "absent" by a consumer of this contract, and this is not a
    //   pedantic point: the portal key itself seeds at that very value
    //   (01.00.00.SqlDataProvider:L77), and the tab, role and module keys all seed at zero
    //   (01.00.00.SqlDataProvider:L140, :L115, :L221), so no numeric value in this schema is reserved
    //   for absence. A nullable integer is the honest wire type: it can carry the null the column
    //   permits, and it can carry the sentinel the legacy application actually wrote. Which of the two
    //   the mapper emits is a mapper decision, taken in Application/Mapping/PortalMappings.cs and
    //   documented there. This contract converts in neither direction, declares no sentinel constant
    //   and offers no absence test.

    /// <summary>
    /// Gets or sets the identifier of the portal's splash page.
    /// </summary>
    /// <remarks>
    /// Corresponds to the legacy <c>SplashTabId</c> property (<c>PortalInfo.vb:L332</c>), to the
    /// <c>cboSplashTabId</c> selector, and to the <c>Portals.SplashTabId</c> column, added
    /// <c>SplashTabId int NULL</c> at <c>03.00.04.SqlDataProvider:L553</c>. The resource entry
    /// <c>plSplashTabId.Text</c> reads "Splash Page:" and <c>plSplashTabId.Help</c> reads "The Splash
    /// Page for your site." A legacy row may carry -1 here where no page was chosen; see the shared
    /// sentinel note above this group of four members, which explains why that value must not be read
    /// as absence.
    /// </remarks>
    public int? SplashTabId { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the portal's home page.
    /// </summary>
    /// <remarks>
    /// Corresponds to the legacy <c>HomeTabId</c> property (<c>PortalInfo.vb:L340</c>), to the
    /// <c>cboHomeTabId</c> selector, and to the <c>Portals.HomeTabId</c> column, added
    /// <c>HomeTabId int NULL</c> at <c>02.00.00.SqlDataProvider:L6677</c>. The resource entry
    /// <c>plHomeTabId.Text</c> reads "Home Page:" and <c>plHomeTabId.Help</c> reads "The Home Page for
    /// your site." A legacy row may carry -1 here where no page was chosen; see the shared sentinel
    /// note above this group of four members.
    /// </remarks>
    public int? HomeTabId { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the portal's login page.
    /// </summary>
    /// <remarks>
    /// Corresponds to the legacy <c>LoginTabId</c> property (<c>PortalInfo.vb:L348</c>), to the
    /// <c>cboLoginTabId</c> selector, and to the <c>Portals.LoginTabId</c> column, added
    /// <c>LoginTabId int NULL</c> at <c>02.00.00.SqlDataProvider:L6678</c>. The resource entry
    /// <c>plLoginTabId.Text</c> reads "Login Page:" and <c>plLoginTabId.Help</c> reads "The Login Page
    /// for your site." A legacy row may carry -1 here where no page was chosen; see the shared sentinel
    /// note above this group of four members.
    /// </remarks>
    public int? LoginTabId { get; set; }

    /// <summary>
    /// Gets or sets the identifier of the portal's user-account page.
    /// </summary>
    /// <remarks>
    /// Corresponds to the legacy <c>UserTabId</c> property (<c>PortalInfo.vb:L356</c>), to the
    /// <c>cboUserTabId</c> selector, and to the <c>Portals.UserTabId</c> column, added
    /// <c>UserTabId int NULL</c> at <c>02.00.00.SqlDataProvider:L6679</c>. The resource entry
    /// <c>plUserTabId.Text</c> reads "User Page:" and <c>plUserTabId.Help</c> reads "The User Page for
    /// your site." A legacy row may carry -1 here where no page was chosen; see the shared sentinel
    /// note above this group of four members.
    /// </remarks>
    public int? UserTabId { get; set; }

    /// <summary>
    /// Gets or sets the portal's default language code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Corresponds to the legacy <c>DefaultLanguage</c> property (<c>PortalInfo.vb:L364</c>), to the
    /// <c>cboDefaultLanguage</c> selector, and to the <c>Portals.DefaultLanguage</c> column. That
    /// column was added <c>DefaultLanguage nvarchar(6) NOT NULL</c> with a database default of
    /// <c>'en-US'</c> at <c>02.02.00.SqlDataProvider:L147</c>, re-asserted at the same width at
    /// <c>03.01.01.SqlDataProvider:L1121</c>, and finally widened to
    /// <c>DefaultLanguage nvarchar(10) NOT NULL</c> at <c>04.03.05.SqlDataProvider:L291</c> - which is
    /// the terminal width, and an illustration of why only the end state of the 88-script chain is
    /// meaningful. The resource entry <c>plDefaultLanguage.Help</c> reads "The Default Language for the
    /// site."
    /// </para>
    /// <para>
    /// The value is a culture code, carried verbatim. This contract neither parses nor canonicalises
    /// it, and the value does not imply that content localisation is available: the legacy
    /// resource-file mechanism is not carried forward by this migration, and user-facing wording is
    /// authored directly in the client. An empty string may legitimately appear here where a modern
    /// reader would expect no value, because the legacy null contract encodes an absent string as the
    /// empty string.
    /// </para>
    /// </remarks>
    public string? DefaultLanguage { get; set; }

    // MIGRATION: two facts about the time-zone member that a downstream reader would otherwise have to
    //   rediscover, one about its NAME and one about its UNIT.
    //
    //   Name. The schema and the legacy class disagree on the capitalisation of the middle syllable.
    //   The column is spelled with a lower-case middle letter -- added as TimezoneOffset at
    //   02.02.00.SqlDataProvider:L151, re-asserted as [TimezoneOffset] at 03.01.01.SqlDataProvider:L1122
    //   and projected under that spelling by the terminal read view at 04.05.00.SqlDataProvider:L1576 --
    //   while the legacy property at PortalInfo.vb:L372 uses the upper-case form, and the schema's own
    //   default-constraint name at 02.02.00.SqlDataProvider:L151 uses the upper-case form too, on the
    //   very same line as the lower-case column. The legacy code worked either way because identifiers
    //   in the database engine are compared case-insensitively. This contract uses the modern
    //   compound-word capitalisation, matching the legacy property; the persisted column name is
    //   untouched and the entity configuration in the infrastructure layer continues to bind the
    //   schema's spelling.
    //
    //   Unit. The value is an offset in MINUTES, not hours. That is measured from the option list the
    //   legacy selector was bound to, Website/App_GlobalResources/TimeZones.xml, whose keys are -720
    //   for twelve hours behind coordinated universal time, -480 for eight hours behind, 0 for
    //   coordinated universal time itself and 60 for one hour ahead. The column's database default is
    //   the bare value -8, set at 02.02.00.SqlDataProvider:L151, which is not a valid key in that
    //   list -- an artefact of an earlier hours-based encoding. That inconsistency is annotated here
    //   and NOT repaired, per the domain-logic-preservation clause; a portal row that still carries
    //   the default will not match any entry in the list, exactly as it did before this migration.

    /// <summary>
    /// Gets or sets the portal's time-zone offset from coordinated universal time, in MINUTES.
    /// </summary>
    /// <remarks>
    /// Corresponds to the legacy <c>TimeZoneOffset</c> property (<c>PortalInfo.vb:L372</c>), to the
    /// <c>cboTimeZone</c> selector - which the legacy screen populated from the stored value at
    /// <c>Website/admin/Portal/SiteSettings.ascx.vb:L425</c> - and to the portal time-zone column,
    /// whose terminal state is an integer that is not nullable
    /// (<c>03.01.01.SqlDataProvider:L1122</c>). The resource entry <c>plTimeZone.Text</c> reads "Portal
    /// TimeZone:" and <c>plTimeZone.Help</c> reads "The TimeZone for the location of the site." The
    /// unit and the spelling of this member are both explained in the migration note above it; a
    /// negative value denotes a zone behind coordinated universal time and carries no other meaning.
    /// </remarks>
    public int? TimeZoneOffset { get; set; }

    /// <summary>
    /// Gets or sets the portal's home directory, the path under which its uploaded content is kept.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Corresponds to the legacy <c>HomeDirectory</c> property (<c>PortalInfo.vb:L380</c>), to the
    /// <c>txtHomeDirectory</c> text box, and to the <c>Portals.HomeDirectory</c> column, added
    /// <c>[HomeDirectory] [varchar](100) NOT NULL</c> with an empty-string default at
    /// <c>02.02.02.SqlDataProvider:L3823</c> and re-asserted at
    /// <c>03.01.01.SqlDataProvider:L1123</c>. The resource entry <c>plHomeDirectory.Help</c> reads
    /// "Enter the Home Directory for this site".
    /// </para>
    /// <para>
    /// The STORED, relative directory value, and nothing else. The legacy class also exposed a
    /// read-only absolute filesystem path derived from this value at <c>PortalInfo.vb:L388</c>; that
    /// member is excluded from every contract in this migration, because it was computed from the
    /// excluded application-path utility module and because an absolute server path is not something a
    /// browser client has any use for. Resolving this value against the content root is the
    /// infrastructure layer's concern, using the hosting environment's own path services.
    /// </para>
    /// <para>
    /// The column defaults to the empty string, and separately the legacy null contract encodes an
    /// absent string as the empty string, so an empty value here is both expected and ambiguous
    /// between those two origins. This contract carries it as given.
    /// </para>
    /// </remarks>
    public string? HomeDirectory { get; set; }

    // MIGRATION: the legacy property name is the all-capitals acronym GUID (PortalInfo.vb:L245); this
    //   member is named with the modern compound capitalisation, Guid. This is a real, externally
    //   observable contract change -- a consumer looking for the legacy spelling in a payload will not
    //   find it -- and it is left visible rather than masked by a serialisation-name attribute, so a
    //   client model can reuse this identifier verbatim without a translation table. The same acronym
    //   convention is applied across the whole Portal contract group, matching the sibling
    //   PortalAliasDto, which renames its own all-capitals acronym members the same way. Stored column
    //   names are untouched; the entity configuration in the infrastructure layer continues to bind the
    //   schema spelling.
    //
    //   The legacy property was one of only two on that class excluded from XML serialisation, and it
    //   is excluded from the twenty-seven-argument update path as well, because the database owns the
    //   value: the column is declared
    //   [GUID] [uniqueidentifier] NOT NULL CONSTRAINT DF_Portals_GUID DEFAULT newid() in the baseline
    //   at 01.00.00.SqlDataProvider:L93, and its terminal alteration is at
    //   03.01.01.SqlDataProvider:L1120 with the generator default re-added at :L1133. It is
    //   nevertheless part of THIS contract, because the settings screen displayed it: read-only, in a
    //   label at sitesettings.ascx:L69, populated from the stored value in upper case at
    //   SiteSettings.ascx.vb:L273.

    /// <summary>
    /// Gets the globally unique identifier of the portal, which the legacy screen displayed read-only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Corresponds to the legacy <c>GUID</c> property (<c>PortalInfo.vb:L245</c>) and to the
    /// <c>Portals.GUID</c> column; the rename and the provenance are both explained in the migration
    /// note above this member. The resource entry <c>plGUID.Help</c> reads "The globally unique
    /// identifier which can be used to identify this portal." The setter exists so that a mapper and a
    /// deserialiser can populate the member; the value is assigned by the database and is not
    /// administrator-editable, which is why no request contract in this group accepts it.
    /// </para>
    /// <para>
    /// Declared as a plain, non-nullable value because the column is not nullable and every persisted
    /// row therefore carries one. The generator behind the column default never produces the all-zero
    /// identifier, so a row written by the database always carries a non-empty value; a value that
    /// travelled through the legacy read path could nonetheless be the all-zero identifier, since that
    /// is what the shared null contract returns as its absent-identifier sentinel
    /// (<c>Library/Components/Shared/Null.vb:L81-L85</c>) and what its absence test reports as absent
    /// (<c>Null.vb:L229-L230</c>). Reporting the value as stored is the schema-faithful behaviour; no
    /// emptiness guard is applied here, because reacting to that sentinel is a service concern.
    /// </para>
    /// </remarks>
    public Guid Guid { get; set; }
}
